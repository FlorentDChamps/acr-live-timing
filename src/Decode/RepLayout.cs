using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACRLiveTiming.Decode
{
    /// <summary>Leaf property kinds of a replicated layout.</summary>
    public enum RepType
    {
        Bool, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double,
        Enum, Name, Str, Text, Object, SoftObject, Array, Delegate, NetSerialize, Unsupported
    }

    /// <summary>
    /// One replication command: a leaf property (struct members flattened into their
    /// owner's handle space) or a dynamic array, whose elements get their own handle
    /// space of <c>1 + index * ElementCmds.Count + cmd</c>.
    /// </summary>
    public sealed class RepCmd
    {
        public string Path { get; init; } = "";          // "DriverData.Nationality"
        public RepType Type { get; init; }
        public string? StructName { get; init; }          // netserialize structs: which one
        public int Bits { get; init; }                    // enums: SerializeInt(value, max) width
        public IReadOnlyList<RepCmd>? ElementCmds { get; init; }   // arrays only
        public override string ToString() => $"{Path}:{Type}";
    }

    /// <summary>A decoded replicated array: element count and the elements present
    /// in this delta (sparse — unchanged elements are not re-sent).</summary>
    public sealed class RepArray
    {
        public int Count { get; init; }
        public SortedDictionary<int, RepObject> Elements { get; } = new();
    }

    /// <summary>Properties decoded from one content block, keyed by command path.
    /// Values: bool, int, uint, long, ulong, float, double, string (name/str/text),
    /// long NetGUID (object), <see cref="RepArray"/>.</summary>
    public sealed class RepObject
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
        /// <summary>The terminating handle 0 was read and every value before it decoded.</summary>
        public bool Complete { get; internal set; }
        /// <summary>Payload bits after the terminator (RPC data or padding).</summary>
        public int BitsLeft { get; internal set; }
        public int HandlesRead { get; internal set; }
        public string? Error { get; internal set; }
        /// <summary>Every handle read, with the bit position of the handle and the
        /// command it selected — the diagnostic trail when a block does not decode.</summary>
        public List<(int handle, int bitPos, string path)> Trace { get; } = new();
        /// <summary>Bit position reached when decoding stopped (error or terminator).</summary>
        public int StopBit { get; internal set; }

        public T? Get<T>(string path) where T : struct
            => Values.TryGetValue(path, out var v) && v is T t ? t : null;
        public string? GetString(string path)
            => Values.TryGetValue(path, out var v) ? v as string : null;
        public RepArray? GetArray(string path)
            => Values.TryGetValue(path, out var v) ? v as RepArray : null;
        public bool Has(string path) => Values.ContainsKey(path);
    }

    /// <summary>
    /// The replicated property layout of one class (see tools/extract-acr-replayout.py
    /// for how it is derived from the UE4SS dump) and the reader that decodes a
    /// content block against it, exactly as FRepLayout::ReceiveProperties does:
    /// packed handle, value, … until handle 0. A block that does not decode cleanly
    /// against a layout is reported incomplete — the check that stands in for the
    /// Replicated flags the dump cannot provide.
    /// </summary>
    public sealed class RepLayout
    {
        public string ClassName { get; }
        public IReadOnlyList<string> WireNames { get; }
        public IReadOnlyList<RepCmd> Cmds { get; }       // handle = index + 1
        /// <summary>Number of leading commands that come from base classes.</summary>
        public int BaseHandles { get; }
        public bool BaseVerified { get; }

        internal RepLayout(string className, IReadOnlyList<string> wireNames, IReadOnlyList<RepCmd> cmds,
            int baseHandles, bool baseVerified)
        {
            ClassName = className; WireNames = wireNames; Cmds = cmds;
            BaseHandles = baseHandles; BaseVerified = baseVerified;
        }

        public override string ToString() => ClassName;

        /// <summary>Diagnostic variant: the class's own commands moved by
        /// <paramref name="delta"/> handles (positive inserts opaque placeholders after
        /// the base commands, negative drops trailing base commands) — used to locate
        /// the true base handle count of a class against a capture.</summary>
        public RepLayout WithBaseShift(int delta)
        {
            var cmds = new List<RepCmd>(Cmds.Take(Math.Max(0, BaseHandles + Math.Min(0, delta))));
            for (int i = 0; i < delta; i++)
                cmds.Add(new RepCmd { Path = $"<pad{i}>", Type = RepType.Unsupported });
            cmds.AddRange(Cmds.Skip(BaseHandles));
            return new RepLayout(ClassName, WireNames, cmds, BaseHandles + delta, false);
        }

        /// <summary>Decode a property-replication content block (HasRepLayout).</summary>
        public RepObject Read(LobbyDecoder.BlockStream.ContentBlock block)
        {
            var obj = new RepObject();
            if (!block.HasRepLayout) { obj.Error = "block has no rep layout"; return obj; }
            var br = new Bits(block.Data, block.BitOffset, block.BitOffset + block.BitLength);
            try
            {
                // FRepLayout::ReceiveProperties starts with the bDoChecksum flag; the
                // game never sets it (a checksum after every property would follow).
                if (br.Bit() != 0) throw new RepReadException("property checksums enabled");
                ReadHandles(br, Cmds, "", obj, obj.Values);
                obj.Complete = obj.Error == null;
            }
            catch (RepReadException ex) { obj.Error = ex.Message; }
            obj.StopBit = br.Pos;
            obj.BitsLeft = Math.Max(0, br.End - br.Pos);
            return obj;
        }

        static void ReadHandles(Bits br, IReadOnlyList<RepCmd> cmds, string prefix, RepObject root,
            Dictionary<string, object?> into)
        {
            while (true)
            {
                int at = br.Pos;
                int handle = br.IntPacked();
                if (handle == 0) { root.Trace.Add((0, at, prefix + "<end>")); return; }
                if (handle < 1 || handle > cmds.Count)
                {
                    root.Trace.Add((handle, at, prefix + "?"));
                    throw new RepReadException($"handle {handle} out of range 1..{cmds.Count} at {prefix}");
                }
                var cmd = cmds[handle - 1];
                root.Trace.Add((handle, at, prefix + cmd.Path));
                root.HandlesRead++;
                into[prefix + cmd.Path] = ReadValue(br, cmd, prefix + cmd.Path, root);
            }
        }

        static void ReadArrayElements(Bits br, RepCmd array, string path, RepObject root, RepArray result)
        {
            var elementCmds = array.ElementCmds!;
            int per = elementCmds.Count;
            if (per == 0) throw new RepReadException($"{path}: array element layout unknown");
            while (true)
            {
                int at = br.Pos;
                int handle = br.IntPacked();
                if (handle == 0) { root.Trace.Add((0, at, path + "<end>")); return; }
                if (handle < 0) throw new RepReadException($"{path}: negative element handle");
                int index = (handle - 1) / per, cmdIndex = (handle - 1) % per;
                root.Trace.Add((handle, at, $"{path}[{index}].{elementCmds[cmdIndex].Path}"));
                if (index >= result.Count)
                    throw new RepReadException($"{path}: element handle {handle} beyond count {result.Count}");
                if (!result.Elements.TryGetValue(index, out var element))
                    result.Elements[index] = element = new RepObject();
                var cmd = elementCmds[cmdIndex];
                root.HandlesRead++;
                element.Values[cmd.Path] = ReadValue(br, cmd, $"{path}[{index}].{cmd.Path}", root);
                element.Complete = true;
            }
        }

        static object? ReadValue(Bits br, RepCmd cmd, string path, RepObject root)
        {
            switch (cmd.Type)
            {
                case RepType.Bool: return br.Bit() != 0;
                case RepType.Int8: return (sbyte)br.Read(8);
                case RepType.UInt8: return (byte)br.Read(8);
                case RepType.Int16: return (short)br.Read(16);
                case RepType.UInt16: return (ushort)br.Read(16);
                case RepType.Int32: return (int)br.Read(32);
                case RepType.UInt32: return (uint)br.Read(32);
                case RepType.Int64: return (long)br.Read(64);
                case RepType.UInt64: return br.Read(64);
                case RepType.Float: return BitConverter.Int32BitsToSingle((int)br.Read(32));
                case RepType.Double: return BitConverter.Int64BitsToDouble((long)br.Read(64));
                case RepType.Enum: return (int)br.Read(cmd.Bits);
                case RepType.Name: return br.FName();
                case RepType.Str: return br.FString();
                case RepType.Text: return br.FText();
                case RepType.Object: return (long)br.IntPacked();     // NetGUID (no export in property data)
                case RepType.SoftObject:
                    // FSoftObjectPath on the wire: one FName-shaped string (verified on
                    // RaceEventDataComponent.GameModeClass, which carries the game mode
                    // configuration name, not an asset path)
                    return br.FName();
                case RepType.Array:
                {
                    int count = (int)br.Read(16);
                    if (count > 4096) throw new RepReadException($"{path}: array count {count}");
                    var arr = new RepArray { Count = count };
                    ReadArrayElements(br, cmd, path, root, arr);
                    return arr;
                }
                case RepType.Delegate:
                    throw new RepReadException($"{path}: delegate property carried data");
                case RepType.NetSerialize when cmd.StructName == "UniqueNetIdRepl":
                    return br.UniqueNetId();
                case RepType.NetSerialize:
                    throw new RepReadException($"{path}: struct with custom NetSerialize (opaque)");
                default:
                    throw new RepReadException($"{path}: unsupported type");
            }
        }

        sealed class RepReadException : Exception { public RepReadException(string m) : base(m) { } }

        /// <summary>LSB-first bit reader bounded to the block's payload.</summary>
        sealed class Bits
        {
            readonly byte[] _d;
            public int Pos;
            public readonly int End;
            public Bits(byte[] d, int start, int end) { _d = d; Pos = start; End = Math.Min(end, d.Length * 8); }

            public int Bit()
            {
                if (Pos >= End) throw new RepReadException($"read past end at bit {Pos}");
                int v = (_d[Pos >> 3] >> (Pos & 7)) & 1; Pos++; return v;
            }
            public ulong Read(int n)
            {
                if (n == 0) return 0;
                if (Pos + n > End) throw new RepReadException($"read {n} bits past end at bit {Pos}");
                ulong v = 0;
                for (int i = 0; i < n; i++) v |= (ulong)Bit() << i;
                return v;
            }
            public int IntPacked()
            {
                long val = 0; int cnt = 0;
                while (true)
                {
                    long by = (long)Read(8);
                    val |= (by >> 1) << (7 * cnt); cnt++;
                    if ((by & 1) == 0) break;
                    if (cnt > 4) throw new RepReadException("packed int too long");
                }
                return (int)val;
            }
            public string FString()
            {
                int len = (int)Read(32);
                if (len == 0) return "";
                bool wide = len < 0;
                int n = wide ? -len : len;
                if (n > 1024) throw new RepReadException($"string length {n}");
                var sb = new StringBuilder(n);
                for (int i = 0; i < n; i++)
                {
                    int c = (int)Read(wide ? 16 : 8);
                    if (i == n - 1)
                    {
                        if (c != 0) throw new RepReadException("string not NUL-terminated");
                    }
                    else sb.Append((char)c);
                }
                return sb.ToString();
            }
            public string FName()
            {
                if (Bit() != 0) return "EName:" + IntPacked();          // hardcoded engine name
                var s = FString();
                int number = (int)Read(32);
                return number == 0 ? s : $"{s}_{number - 1}";
            }
            /// <summary>FUniqueNetIdRepl::NetSerialize: an encoding-flags byte (bit 0
            /// encoded as bytes, bit 1 empty, bits 3-7 a hash of the online-service type,
            /// 31 = a type named explicitly), the type name as an FString when named, then
            /// either a byte-counted binary id or the id as an FString. Returns
            /// "type:contents" (hex for binary ids). Verified on the wire: 0xF8, "EOSPlus",
            /// "&lt;steamid64&gt;_+_|&lt;32-hex EOS puid&gt;".</summary>
            public string UniqueNetId()
            {
                int flags = (int)Read(8);
                if ((flags & 2) != 0) return "";
                int typeHash = flags >> 3;
                string type = typeHash is 0 or 31 ? FString() : "type" + typeHash;
                if ((flags & 1) != 0)
                {
                    int size = (int)Read(8);
                    var sb = new StringBuilder(size * 2);
                    for (int i = 0; i < size; i++) sb.Append(((byte)Read(8)).ToString("x2"));
                    return type + ":" + sb;
                }
                return type + ":" + FString();
            }

            public string FText()
            {
                Read(32);                                    // flags
                int history = (sbyte)Read(8);
                switch (history)
                {
                    case -1:                                 // None: optional culture-invariant string
                        return Read(32) != 0 ? FString() : "";
                    case 0:                                  // Base: namespace, key, source
                        FString(); FString(); return FString();
                    default:
                        throw new RepReadException($"FText history type {history}");
                }
            }
        }
    }

    /// <summary>Layouts loaded from the embedded acr-replayout.json.</summary>
    public static class RepLayouts
    {
        sealed class Doc
        {
            public Dictionary<string, ClassDef> Classes { get; set; } = new();
            public Dictionary<string, List<PropDef>> Structs { get; set; } = new();
        }
        sealed class ClassDef
        {
            public string Class { get; set; } = "";
            public int BaseHandles { get; set; }
            public bool BaseVerified { get; set; }
            public List<PropDef> Properties { get; set; } = new();
            public List<string> WireNames { get; set; } = new();
        }
        sealed class PropDef
        {
            public string Name { get; set; } = "";
            public string Type { get; set; } = "";
            public string? Struct { get; set; }
            public string? Enum { get; set; }
            public int Bits { get; set; }
            public PropDef? Element { get; set; }
            public bool Inherited { get; set; }
        }

        static readonly Lazy<(List<RepLayout> all, Dictionary<string, RepLayout> byWire, Dictionary<string, RepLayout> byClass)> Data = new(Load);

        public static IReadOnlyList<RepLayout> All => Data.Value.all;
        public static RepLayout? ForWireName(string name) => Data.Value.byWire.GetValueOrDefault(name);
        public static RepLayout? ForClass(string className) => Data.Value.byClass.GetValueOrDefault(className);

        static (List<RepLayout>, Dictionary<string, RepLayout>, Dictionary<string, RepLayout>) Load()
        {
            var all = new List<RepLayout>();
            var byWire = new Dictionary<string, RepLayout>(StringComparer.Ordinal);
            var byClass = new Dictionary<string, RepLayout>(StringComparer.Ordinal);
            try
            {
                var assembly = typeof(RepLayouts).Assembly;
                var resource = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("acr-replayout.json", StringComparison.Ordinal));
                if (resource == null) return (all, byWire, byClass);
                using var stream = assembly.GetManifestResourceStream(resource);
                var doc = stream == null ? null : JsonSerializer.Deserialize<Doc>(stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (doc == null) return (all, byWire, byClass);
                foreach (var (name, def) in doc.Classes)
                {
                    var cmds = new List<RepCmd>();
                    Flatten(def.Properties.Where(p => p.Inherited).ToList(), "", doc.Structs, cmds, 0);
                    int baseHandles = cmds.Count;
                    Flatten(def.Properties.Where(p => !p.Inherited).ToList(), "", doc.Structs, cmds, 0);
                    var layout = new RepLayout(name, def.WireNames, cmds, baseHandles, def.BaseVerified);
                    all.Add(layout);
                    byClass[name] = layout;
                    foreach (var w in def.WireNames) byWire[w] = layout;
                }
            }
            catch (JsonException) { /* a bad layout file disables layout decoding, nothing else */ }
            return (all, byWire, byClass);
        }

        // Struct members join their owner's handle space (one command per leaf);
        // a dynamic array is one command with its own element command list.
        static void Flatten(List<PropDef> props, string prefix, Dictionary<string, List<PropDef>> structs,
            List<RepCmd> into, int depth)
        {
            if (depth > 8) return;
            foreach (var p in props)
            {
                string path = prefix + p.Name;
                if (p.Type == "struct" && p.Struct != null && structs.TryGetValue(p.Struct, out var members))
                {
                    Flatten(members, path + ".", structs, into, depth + 1);
                    continue;
                }
                into.Add(ToCmd(p, path, structs, depth));
            }
        }

        static RepCmd ToCmd(PropDef p, string path, Dictionary<string, List<PropDef>> structs, int depth)
        {
            if (p.Type == "array" && p.Element != null)
            {
                var element = new List<RepCmd>();
                if (p.Element.Type == "struct" && p.Element.Struct != null
                    && structs.TryGetValue(p.Element.Struct, out var members))
                    Flatten(members, "", structs, element, depth + 1);
                else
                    element.Add(ToCmd(p.Element, "Value", structs, depth + 1));
                return new RepCmd { Path = path, Type = RepType.Array, ElementCmds = element };
            }
            return new RepCmd
            {
                Path = path,
                Bits = p.Bits,
                StructName = p.Struct,
                Type = p.Type switch
                {
                    "bool" => RepType.Bool, "int8" => RepType.Int8, "uint8" => RepType.UInt8,
                    "int16" => RepType.Int16, "uint16" => RepType.UInt16, "int32" => RepType.Int32,
                    "uint32" => RepType.UInt32, "int64" => RepType.Int64, "uint64" => RepType.UInt64,
                    "float" => RepType.Float, "double" => RepType.Double, "enum" => RepType.Enum,
                    "name" => RepType.Name, "str" => RepType.Str, "text" => RepType.Text,
                    "object" => RepType.Object, "softobject" => RepType.SoftObject,
                    "delegate" => RepType.Delegate, "netserialize" => RepType.NetSerialize,
                    _ => RepType.Unsupported
                }
            };
        }
    }
}
