#!/usr/bin/env python3
"""Generate src/Content/acr-replayout.json from a UE4SS object dump.

The JSON describes, for the ACR classes the decoder reads, the replicated
property layout Unreal Engine's FRepLayout derives from the class: property
handles are assigned in class declaration order, base class first, one handle
per leaf property (struct members are flattened, a dynamic array is one handle
whose elements carry their own handle space of `1 + index * cmdsPerElement +
cmd`). The dump lists properties in declaration order with their types, nested
structs, array element types and enum values, which is everything the layout
needs — except the Replicated flag, which UE4SS does not print. Engine base
classes therefore come from the KNOWN_ENGINE_REPS table (their replicated
properties are documented in the engine source); game classes are assumed to
replicate every replicable property, and the decoder validates a layout by
requiring each replicated block to parse to its exact end.

Usage:
    python tools/extract-acr-replayout.py [DUMP] [OUTPUT]
Defaults: inputs/UE4SS_ObjectDump_0.6/UE4SS_ObjectDump.txt and
src/Content/acr-replayout.json, relative to the repository root.
"""
from __future__ import annotations

import json
import math
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_DUMP = ROOT / "inputs" / "UE4SS_ObjectDump_0.6" / "UE4SS_ObjectDump.txt"
DEFAULT_OUT = ROOT / "src" / "Content" / "acr-replayout.json"

# Classes the decoder reads: class name -> names under which the object is
# recognised on the wire. Components are exported under their instance name
# (the class name without the "Component" suffix); actors are recognised by the
# archetype path exported with their spawn ("Default__BC_RaceGameState_C").
# Blueprint classes are found by their short name anywhere under /Game/.
CLASSES = {
    "RaceStateData": ["RaceStateData"],
    "RaceSectorsPlayerData": ["RaceSectorsPlayerData"],
    "PlayerRaceSectorsTracker": ["PlayerRaceSectorsTracker"],
    "RaceParticipantDataComponent": ["RaceParticipantData"],
    "RaceEventRallyResultsComponent": ["RaceEventRallyResults"],
    "RaceEventDataComponent": ["RaceEventData"],
    "RaceLobbyDataComponent": ["RaceLobbyData"],
    "AcrGameState": ["AcrGameState"],
    "BC_RaceGameState_C": ["Default__BC_RaceGameState_C"],
    "BC_RaceParticipant_C": ["Default__BC_RaceParticipant_C"],
    "BC_RacePlayerState_C": ["Default__BC_RacePlayerState_C"],
}

# Replicated properties of engine base classes (UE 5.6 GetLifetimeReplicatedProps);
# handles follow the dump (declaration) order among these. Everything above has none.
KNOWN_ENGINE_REPS = {
    "/Script/CoreUObject.Object": set(),
    "/Script/Engine.ActorComponent": {"bReplicates", "bIsActive"},
    "/Script/Engine.Actor": {
        "bReplicateMovement", "bHidden", "bTearOff", "bCanBeDamaged",
        "ReplicatedMovement", "AttachmentReplication", "Owner", "Role",
        "RemoteRole", "Instigator",
    },
    "/Script/Engine.Info": set(),
    "/Script/Engine.GameStateBase": {
        "GameModeClass", "SpectatorClass", "bReplicatedHasBegunPlay",
        "ReplicatedWorldTimeSeconds", "ReplicatedWorldTimeSecondsDouble",
        "ServerWorldTimeSecondsDelta",
    },
    "/Script/Engine.GameState": {"MatchState", "ElapsedTime"},
    # Kunos' DM framework: TravelTrackId sits at handle 24 on the wire, so only two of
    # the three DMGameState flags replicate; which one does not cannot be told apart
    # (all three precede it and none has been seen replicated) — the client flag is
    # the natural non-replicated one.
    "/Script/dmengine.DMGameState": {"bSimulatePhysicsOnServer", "bRecordReplayOnServer"},
    # verified on the wire: StartTime at 24 (world seconds at join), UniqueID at 25,
    # PlayerNamePrivate at 26 -> bFromPreviousLevel replicates, PawnPrivate does not
    "/Script/Engine.PlayerState": {
        "Score", "PlayerId", "CompressedPing", "bIsSpectator", "bOnlySpectator",
        "bIsABot", "bIsInactive", "bFromPreviousLevel", "StartTime", "UniqueID",
        "PlayerNamePrivate",
    },
}

# Game properties that are declared but not replicated (no handle on the wire).
# Established by decoding: with ResolvedCarClass counted, RaceParticipantData blocks
# hit a terminator right after RunningOrder with the tyre allocation still unread.
NOT_REPLICATED = {
    "/Script/acr.RaceParticipantDataComponent:ResolvedCarClass",
}

# TEnumAsByte properties: the dump shows a plain ByteProperty, but the engine
# net-serializes them with the enum width (SerializeInt(value, MaxEnumValue)).
BYTE_ENUMS = {
    "/Script/Engine.Actor:Role": "/Script/Engine.ENetRole",
    "/Script/Engine.Actor:RemoteRole": "/Script/Engine.ENetRole",
}

# Structs with a custom NetSerialize (WithNetSerializer trait): FRepLayout keeps
# them as ONE opaque command instead of flattening their members.
# FRepAttachment is NOT one of them: it flattens into six commands (its vectors and
# rotator each being one), which is what puts APlayerState.CompressedPing at
# handle 18 on the wire (Actor contributes 15 handles).
NETSERIALIZE_STRUCTS = {
    "RepMovement", "UniqueNetIdRepl", "Vector", "Vector_NetQuantize",
    "Vector_NetQuantize10", "Vector_NetQuantize100", "Vector_NetQuantizeNormal",
    "Rotator", "Quat", "GameplayTag", "GameplayTagContainer", "PredictionKey",
}

# Property kinds that can never be marked Replicated (UHT rejects them), so they
# take no handle. Verified on the 0.6 capture: RaceSectorsPlayerData.SectorsRecords
# and RaceEventRallyResultsComponent.ParticipantsResults both sit at handle 3,
# right after ActorComponent's two, with their OnXxx delegates taking none.
UNREPLICABLE = {
    "MapProperty", "SetProperty", "FieldPathProperty", "InterfaceProperty",
    "MulticastInlineDelegateProperty", "MulticastSparseDelegateProperty", "DelegateProperty",
}

SCALARS = {
    "IntProperty": "int32", "UInt32Property": "uint32", "Int64Property": "int64",
    "UInt64Property": "uint64", "Int16Property": "int16", "UInt16Property": "uint16",
    "Int8Property": "int8", "FloatProperty": "float", "DoubleProperty": "double",
    "BoolProperty": "bool", "NameProperty": "name", "StrProperty": "str",
    "TextProperty": "text", "ObjectProperty": "object", "ClassProperty": "object",
    "WeakObjectProperty": "object", "SoftObjectProperty": "softobject",
    "SoftClassProperty": "softobject",
}

LINE_RE = re.compile(r"^\[([0-9A-F]{16})\] (\S+) (\S+)(.*)$")
TAG_RE = re.compile(r"\[(\w+): ([^\]]*)\]")


class Obj:
    __slots__ = ("addr", "kind", "name", "tags", "children", "values")

    def __init__(self, addr: str, kind: str, name: str, tags: dict[str, str]):
        self.addr, self.kind, self.name, self.tags = addr, kind, name, tags
        self.children: list[Obj] = []
        self.values: dict[str, int] = {}


def parse_dump(path: Path) -> dict[str, Obj]:
    objects: dict[str, Obj] = {}
    by_name: dict[str, Obj] = {}
    last_enum: Obj | None = None
    with path.open(encoding="utf-8", errors="replace") as f:
        for line in f:
            m = LINE_RE.match(line.rstrip("\n"))
            if not m:
                continue
            addr, kind, name, rest = m.groups()
            tags = dict(TAG_RE.findall(rest))
            if addr == "0" * 16 and "v" in tags and last_enum is not None:
                # enum value line: "[0000...] ERacePhase::Ended [n: ..] [v: 7]" (engine
                # enums list bare names: "ROLE_Authority")
                last_enum.values[kind.split("::", 1)[-1]] = int(tags["v"])
                continue
            obj = Obj(addr, kind, name, tags)
            objects[addr] = obj
            if kind in ("Class", "BlueprintGeneratedClass", "ScriptStruct", "Enum", "Function", "DelegateFunction"):
                by_name[name] = obj
                by_name["short:" + name.split(".")[-1]] = obj
            if kind == "Enum":
                last_enum = obj
    # attach properties to their owner (class, struct, array or function)
    for obj in objects.values():
        owner = obj.tags.get("owr")
        if owner and owner in objects and obj.kind.endswith("Property"):
            objects[owner].children.append(obj)
    objects.update({"name:" + k: v for k, v in by_name.items()})
    return objects


def enum_bits(enum: Obj) -> int:
    # FEnumProperty/FByteProperty net-serialize with SerializeInt(value, MaxEnumValue),
    # where UEnum::GetMaxEnumValue() is the highest listed value (the _MAX entry) + 1:
    # ceil(log2(max + 1)) bits. Verified on the wire: ENetRole (MAX = 4) takes 3 bits,
    # ERacePhase (MAX = 13) takes 4.
    mx = max(enum.values.values()) if enum.values else 255
    return math.ceil(math.log2(mx + 1)) if mx >= 1 else 0


class Extractor:
    def __init__(self, objects: dict[str, Obj]):
        self.objects = objects
        self.structs: dict[str, list[dict]] = {}
        self.enums: dict[str, dict] = {}
        self.warnings: list[str] = []

    def prop_entry(self, prop: Obj, owner_label: str) -> dict | None:
        kind = prop.kind
        short = prop.name.split(":")[-1]
        if kind in UNREPLICABLE or prop.name in NOT_REPLICATED:
            return None
        if kind in SCALARS:
            return {"name": short, "type": SCALARS[kind]}
        if kind in ("EnumProperty", "ByteProperty"):
            enum_addr = prop.tags.get("em")
            if prop.name in BYTE_ENUMS:
                enum_addr = self.objects["name:" + BYTE_ENUMS[prop.name]].addr
            if enum_addr and enum_addr in self.objects:
                enum = self.objects[enum_addr]
                self.enums[enum.name.split(".")[-1]] = {"values": enum.values}
                return {"name": short, "type": "enum", "enum": enum.name.split(".")[-1],
                        "bits": enum_bits(enum)}
            return {"name": short, "type": "uint8"}
        if kind == "StructProperty":
            struct = self.objects.get(prop.tags.get("ss", ""))
            if struct is None:
                self.warnings.append(f"{owner_label}.{short}: struct definition missing")
                return None
            sname = struct.name.split(".")[-1]
            if sname in NETSERIALIZE_STRUCTS:
                return {"name": short, "type": "netserialize", "struct": sname}
            self.add_struct(struct)
            return {"name": short, "type": "struct", "struct": sname}
        if kind == "ArrayProperty":
            inner = self.objects.get(prop.tags.get("ai", ""))
            if inner is None:
                self.warnings.append(f"{owner_label}.{short}: array element type missing")
                return None
            element = self.prop_entry(inner, owner_label + "." + short)
            if element is None:
                self.warnings.append(f"{owner_label}.{short}: array of unreplicable element")
                return None
            element.pop("name", None)
            return {"name": short, "type": "array", "element": element}
        self.warnings.append(f"{owner_label}.{short}: unsupported property kind {kind}")
        return None

    def add_struct(self, struct: Obj) -> None:
        sname = struct.name.split(".")[-1]
        if sname in self.structs:
            return
        self.structs[sname] = []   # placeholder first: guards against recursive structs
        members = []
        # a struct's super struct contributes its members first
        sup = self.objects.get(struct.tags.get("sps", ""))
        if sup is not None and sup.addr != "0" * 16:
            self.add_struct(sup)
            members.extend(self.structs[sup.name.split(".")[-1]])
        for prop in struct.children:
            entry = self.prop_entry(prop, sname)
            if entry is not None:
                members.append(entry)
        self.structs[sname] = members

    def class_chain(self, cls: Obj) -> list[Obj]:
        chain = []
        cur: Obj | None = cls
        while cur is not None:
            chain.append(cur)
            sup = cur.tags.get("sps", "")
            cur = self.objects.get(sup) if sup and sup != "0" * 16 else None
        chain.reverse()   # base first
        return chain

    def extract_class(self, cls: Obj) -> dict:
        props: list[dict] = []
        base_handles = 0
        verified_base = True
        for c in self.class_chain(cls):
            if c.name in KNOWN_ENGINE_REPS:
                wanted = set(KNOWN_ENGINE_REPS[c.name])
                for prop in c.children:
                    name = prop.name.split(":")[-1]
                    if name not in wanted:
                        continue
                    wanted.discard(name)
                    entry = self.prop_entry(prop, c.name.split(".")[-1])
                    if entry is not None:
                        entry["inherited"] = True
                        props.append(entry)
                for name in sorted(wanted):
                    self.warnings.append(f"{c.name}: known replicated property {name} not in dump")
                continue
            if c.name.startswith("/Script/Engine.") or c.name.startswith("/Script/CoreUObject."):
                own = [p for p in c.children if p.kind.endswith("Property") and p.kind not in UNREPLICABLE]
                if own:
                    verified_base = False
                    self.warnings.append(
                        f"{cls.name}: engine base {c.name} has {len(own)} properties with unknown replication")
                continue
            for prop in c.children:
                entry = self.prop_entry(prop, c.name.split(".")[-1])
                if entry is not None:
                    if c is not cls:
                        entry["inherited"] = True
                    props.append(entry)
        base_handles = sum(1 for p in props if p.get("inherited"))
        return {
            "class": cls.name,
            "super": [c.name.split(".")[-1] for c in self.class_chain(cls)[:-1]],
            "baseHandles": base_handles,
            "baseVerified": verified_base,
            "properties": props,
        }


def main(argv: list[str]) -> int:
    dump = Path(argv[1]) if len(argv) > 1 else DEFAULT_DUMP
    out = Path(argv[2]) if len(argv) > 2 else DEFAULT_OUT
    objects = parse_dump(dump)
    ex = Extractor(objects)
    classes = {}
    for cname, wire in CLASSES.items():
        cls = objects.get("name:/Script/acr." + cname) or objects.get("name:short:" + cname)
        if cls is None:
            ex.warnings.append(f"class {cname} not found in dump")
            continue
        entry = ex.extract_class(cls)
        entry["wireNames"] = wire
        classes[cname] = entry
    doc = {
        "schemaVersion": 1,
        "source": dump.parent.name + "/" + dump.name,
        "classes": classes,
        "structs": ex.structs,
        "enums": ex.enums,
    }
    out.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    for w in ex.warnings:
        print("warning:", w, file=sys.stderr)
    print(f"wrote {out} ({len(classes)} classes, {len(ex.structs)} structs, {len(ex.enums)} enums)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
