#!/usr/bin/env python3
"""Generate the ACR content catalog from FModel JSON exports."""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Any


SCRIPT_ROOT = Path(__file__).resolve().parent

# ISO 3166-1 alpha-2 code (or gb-* subdivision) of every DT_Countries row, keyed by
# the row id exactly as the game spells it (its "lAnd" rows included); None for a row
# that has no flag. The board loads flag images by this code, which the game data does
# not carry (it only names its flag textures). Every row must be listed: the generator
# fails otherwise, so a game update that adds or renames a country is caught when the
# catalog is regenerated.
COUNTRY_ISO: dict[str, str | None] = {
    "Afghanistan": "af", "Albania": "al", "Algeria": "dz", "Andorra": "ad",
    "Angola": "ao", "AntiguaAndBarbuda": "ag", "Argentina": "ar", "Armenia": "am",
    "Australia": "au", "Austria": "at", "Azerbaijan": "az", "Bahamas": "bs",
    "Bahrain": "bh", "Bangladesh": "bd", "Barbados": "bb", "Belarus": "by",
    "Belgium": "be", "Belize": "bz", "Benin": "bj", "Bolivia": "bo",
    "BosniaAndHerzegovina": "ba", "Botswana": "bw", "Brazil": "br", "Brunei": "bn",
    "Bulgaria": "bg", "Burkinafaso": "bf", "Burundi": "bi", "CaboVerde": "cv",
    "Cambodia": "kh", "Cameroon": "cm", "Canada": "ca", "CentralAfricanRepublic": "cf",
    "Chad": "td", "Chile": "cl", "China": "cn", "Colombia": "co",
    "Comoros": "km", "Costarica": "cr", "CoteDivoire": "ci", "Croatia": "hr",
    "Cuba": "cu", "Cyprus": "cy", "CzechRepublic": "cz", "DemocraticRepublicOfTheCongo": "cd",
    "Denmark": "dk", "Djibouti": "dj", "Dominica": "dm", "DominicanRepublic": "do",
    "EastTimor": "tl", "Ecuador": "ec", "Egypt": "eg", "Elsalvador": "sv",
    "EquatorialGuinea": "gq", "Eritrea": "er", "Estonia": "ee", "Eswatini": "sz",
    "Ethiopia": "et", "Fiji": "fj", "FinlAnd": "fi", "France": "fr",
    "Gabon": "ga", "Gambia": "gm", "Georgia": "ge", "Germany": "de",
    "Ghana": "gh", "Greece": "gr", "Grenada": "gd", "Guatemala": "gt",
    "Guinea": "gn", "GuineaBissau": "gw", "Guyana": "gy", "Haiti": "ht",
    "Honduras": "hn", "HongKong": "hk", "Hungary": "hu", "IcelAnd": "is",
    "India": "in", "Indonesia": "id", "Iran": "ir", "Iraq": "iq",
    "IrelAnd": "ie", "Israel": "il", "Italy": "it", "Jamaica": "jm",
    "Japan": "jp", "Jordan": "jo", "Kazakhstan": "kz", "Kenya": "ke",
    "Kiribati": "ki", "Kuwait": "kw", "Kyrgyzstan": "kg", "Laos": "la",
    "Latvia": "lv", "Lebanon": "lb", "Lesotho": "ls", "Liberia": "lr",
    "Libya": "ly", "Liechtenstein": "li", "Lithuania": "lt", "Luxembourg": "lu",
    "Macau": "mo", "Madagascar": "mg", "Malawi": "mw", "Malaysia": "my",
    "Maldives": "mv", "Mali": "ml", "Malta": "mt", "Mauritania": "mr",
    "Mauritius": "mu", "Mexico": "mx", "Micronesia": "fm", "Moldova": "md",
    "Monaco": "mc", "Mongolia": "mn", "Montenegro": "me", "Morocco": "ma",
    "Mozambique": "mz", "Myanmar": "mm", "Namibia": "na", "Nauru": "nr",
    "Nepal": "np", "Netherlands": "nl", "NewZealand": "nz", "Nicaragua": "ni",
    "Niger": "ne", "Nigeria": "ng", "NorthKorea": "kp", "NorthMacedonia": "mk",
    "Norway": "no", "Oman": "om", "Other": None, "Pakistan": "pk", "Palau": "pw",
    "Panama": "pa", "PapuaNewGuinea": "pg", "Paraguay": "py", "Peru": "pe",
    "Philippines": "ph", "PolAnd": "pl", "Portugal": "pt", "Qatar": "qa",
    "RepublicOfTheCongo": "cg", "Romania": "ro", "Russia": "ru", "Rwanda": "rw",
    "SaintKittsAndNevis": "kn", "SaintLucia": "lc", "SaintVincentAndThegrenadines": "vc", "Samoa": "ws",
    "Sanmarino": "sm", "SaotomeAndPrincipe": "st", "SaudiArabia": "sa", "Senegal": "sn",
    "Serbia": "rs", "Seychelles": "sc", "SierraLeone": "sl", "Singapore": "sg",
    "Slovakia": "sk", "Slovenia": "si", "SolomonIslands": "sb", "Somalia": "so",
    "SouthAfrica": "za", "SouthKorea": "kr", "SouthSudan": "ss", "Spain": "es",
    "Srilanka": "lk", "Sudan": "sd", "Suriname": "sr", "Sweden": "se",
    "Switzerland": "ch", "Syria": "sy", "Taiwan": "tw", "Tajikistan": "tj",
    "Tanzania": "tz", "ThailAnd": "th", "Togo": "tg", "Tonga": "to",
    "TrinidadAndTobago": "tt", "Tunisia": "tn", "Turkey": "tr", "Turkmenistan": "tm",
    "Tuvalu": "tv", "Uganda": "ug", "Ukraine": "ua", "UnitedArabemirates": "ae",
    "UnitedKingdom": "gb", "UnitedStates": "us", "Uruguay": "uy", "Uzbekistan": "uz",
    "Vanuatu": "vu", "Vaticancity": "va", "Venezuela": "ve", "Vietnam": "vn",
    "Wales": "gb-wls", "Yemen": "ye", "Zambia": "zm", "Zimbabwe": "zw",
}


class CatalogError(ValueError):
    """Raised when the generated catalog would contain inconsistent data."""


class CatalogGenerator:
    def __init__(self, input_directory: Path, output_path: Path) -> None:
        self.input_directory = input_directory
        self.output_path = output_path
        self.normalizations = 0

    def read_data_table(self, name: str) -> dict[str, Any]:
        with (self.input_directory / name).open(encoding="utf-8") as source:
            document = json.load(source)
        return document[0]["Rows"]

    def normalize_text(self, text: str | None, context: str) -> str | None:
        if text is None:
            return None
        normalized = text.strip()
        if normalized != text:
            self.normalizations += 1
            print(
                f"WARNING: Normalized leading/trailing whitespace in {context}: {text!r}",
                file=sys.stderr,
            )
        return normalized

    def localized_name(self, value: dict[str, Any] | None, context: str) -> str | None:
        if value is None:
            return None
        return self.normalize_text(
            value.get("LocalizedString") or value.get("SourceString"), context
        )

    def available_car_classes(self, available_cars: dict[str, Any]) -> dict[str, str]:
        """Return the playable Cars-row ids and their class from DT_AvailableCars."""
        result: dict[str, str] = {}
        for menu_id, menu_row in available_cars.items():
            if not menu_id.startswith("Car_"):
                continue
            for class_entry in menu_row.get("CarClasses", []):
                class_id = str(class_entry["Key"]["Row"])
                for car in class_entry["Value"].get("Cars", []):
                    car_id = str(car["Row"])
                    existing = result.setdefault(car_id, class_id)
                    if existing != class_id:
                        raise CatalogError(
                            f"Catalog validation failed: car '{car_id}' is assigned to both "
                            f"'{existing}' and '{class_id}' in DT_AvailableCars."
                        )
        if not result:
            raise CatalogError("Catalog validation failed: DT_AvailableCars contains no cars.")
        return result

    @staticmethod
    def assert_unique_ids(items: list[dict[str, Any]], kind: str) -> None:
        identifiers = [str(item.get("id", "")) for item in items]
        if any(not identifier.strip() for identifier in identifiers):
            raise CatalogError(f"Catalog validation failed: {kind} contains an empty id.")
        duplicates = [identifier for identifier, count in Counter(identifiers).items() if count > 1]
        if duplicates:
            raise CatalogError(
                f"Catalog validation failed: duplicate {kind} id(s): {', '.join(duplicates)}."
            )

    @classmethod
    def validate_catalog(cls, catalog: dict[str, Any]) -> None:
        cls.assert_unique_ids(catalog["locations"], "location")
        cls.assert_unique_ids(catalog["countries"], "country")
        cls.assert_unique_ids(catalog["stages"], "stage")
        cls.assert_unique_ids(catalog["cars"], "car")

        # every spelling must resolve to exactly one country, case-insensitively
        spellings: dict[str, str] = {}
        for country in catalog["countries"]:
            if not country["name"] or not country["name"].strip():
                raise CatalogError(
                    f"Catalog validation failed: country '{country['id']}' has no display name."
                )
            for spelling in [country["id"], *country["aliases"]]:
                owner = spellings.setdefault(spelling.casefold(), country["id"])
                if owner != country["id"]:
                    raise CatalogError(
                        f"Catalog validation failed: country spelling '{spelling}' belongs to both "
                        f"'{owner}' and '{country['id']}'."
                    )

        location_ids: set[str] = set()
        for location in catalog["locations"]:
            location_ids.add(location["id"])
            country_id = location["country"]["id"]
            if not location["name"] or not country_id or not country_id.strip():
                raise CatalogError(
                    f"Catalog validation failed: location '{location['id']}' has incomplete metadata."
                )
            surface_total = sum(float(surface["percentage"]) for surface in location["surfaces"])
            if not location["surfaces"] or abs(surface_total - 1.0) > 0.000001:
                raise CatalogError(
                    f"Catalog validation failed: location '{location['id']}' surface percentages must total 1."
                )

        wire_ids = [stage["wireId"] for stage in catalog["stages"] if "wireId" in stage]
        duplicates = sorted(
            identifier for identifier, count in Counter(wire_ids).items() if count > 1
        )
        if duplicates:
            raise CatalogError(
                f"Catalog validation failed: duplicate stage wireId(s): {', '.join(duplicates)}."
            )
        for stage in catalog["stages"]:
            if not stage["name"] or not stage["name"].strip():
                raise CatalogError(
                    f"Catalog validation failed: stage '{stage['id']}' has no display name."
                )
            if stage["known"] and stage["locationId"] not in location_ids:
                raise CatalogError(
                    f"Catalog validation failed: stage '{stage['id']}' references unknown "
                    f"location '{stage['locationId']}'."
                )
            if not isinstance(stage.get("lengthKm"), (int, float)) or stage["lengthKm"] <= 0:
                raise CatalogError(
                    f"Catalog validation failed: stage '{stage['id']}' has no positive length."
                )

        for car in catalog["cars"]:
            if any(
                not value or not value.strip()
                for value in (car["displayName"], car["class"]["id"], car["group"]["id"])
            ):
                raise CatalogError(
                    f"Catalog validation failed: car '{car['id']}' has an incomplete "
                    "class/group relationship."
                )

    def generate(self) -> dict[str, Any]:
        cars = self.read_data_table("DT_Cars.json")
        available_cars = self.read_data_table("DT_AvailableCars.json")
        classes = self.read_data_table("DT_CarsClasses.json")
        groups = self.read_data_table("DT_CarsGroups.json")
        manufacturers = self.read_data_table("DT_Manufacturers.json")
        countries = self.read_data_table("DT_Countries.json")
        country_flags = self.read_data_table("DT_CountryFlags.json")
        tracks = self.read_data_table("DT_Tracks.json")
        locations = self.read_data_table("DT_TracksLocations.json")
        variants = self.read_data_table("DT_TracksVariants.json")

        catalog_locations: list[dict[str, Any]] = []
        for identifier, location in sorted(locations.items()):
            country_id = str(location["Country"]["Row"])
            country = countries[country_id]
            catalog_locations.append(
                {
                    "id": identifier,
                    "name": self.localized_name(location.get("Name"), f"location '{identifier}'"),
                    "country": {
                        "id": country_id,
                        "name": self.localized_name(country.get("Name"), f"country '{country_id}'"),
                    },
                    "surfaces": [
                        {
                            "id": str(surface["SurfaceType"]["Row"]),
                            "percentage": surface["Percentage"],
                        }
                        for surface in location["SurfaceTypesData"]
                    ],
                }
            )

        catalog_countries: list[dict[str, Any]] = []
        for identifier, country in sorted(countries.items()):
            icon_row = str(country["Icon"]["Row"])
            texture = str(country_flags[icon_row]["Value"]["AssetPathName"])
            texture_name = texture.rsplit("/", 1)[-1].split(".")[0]
            if texture_name.startswith("T_"):
                texture_name = texture_name[2:]
            # every spelling a client may replicate for this row: the row id, the flag
            # row id (differs in casing for some rows) and the flag texture name (the
            # pre-rename spelling of several rows)
            aliases = {icon_row, texture_name}
            aliases.discard(identifier)
            if identifier not in COUNTRY_ISO:
                raise CatalogError(
                    f"Catalog validation failed: country '{identifier}' has no ISO code in COUNTRY_ISO."
                )
            catalog_countries.append(
                {
                    "id": identifier,
                    "name": self.localized_name(country.get("Name"), f"country '{identifier}'"),
                    "iso": COUNTRY_ISO[identifier],
                    "aliases": sorted(aliases, key=str.casefold),
                    "available": bool(country.get("bAvailableToPlayer", True)),
                }
            )
        catalog_stages: list[dict[str, Any]] = []
        for wire_id, variant in sorted(variants.items()):
            canonical_id = str(variant["CarsData"]["Row"])
            track_id = str(variant["Track"]["Row"])
            track = tracks[track_id]
            location_id = str(track["Location"]["Row"])
            catalog_stages.append(
                {
                    "id": canonical_id,
                    "wireId": wire_id,
                    "locationId": location_id,
                    "trackId": track_id,
                    "name": self.localized_name(variant.get("StageName"), f"stage '{canonical_id}'"),
                    "shortName": self.localized_name(variant.get("Name"), f"stage '{canonical_id}'"),
                    "lengthKm": variant["Length"],
                    "known": True,
                }
            )
        catalog_stages.sort(key=lambda stage: stage["id"])

        catalog_cars: list[dict[str, Any]] = []
        for identifier, class_id in sorted(self.available_car_classes(available_cars).items()):
            car_class = classes[class_id]
            group_id = str(car_class["Group"]["Row"])
            group = groups[group_id]
            car = cars[identifier]
            car_class_id = str(car["Class"]["Row"])
            if car_class_id != class_id:
                raise CatalogError(
                    f"Catalog validation failed: car '{identifier}' is '{car_class_id}' in DT_Cars "
                    f"but '{class_id}' in DT_AvailableCars."
                )
            manufacturer_id = str(car["Manufacturer"]["Row"])
            manufacturer = self.localized_name(
                manufacturers[manufacturer_id].get("Name"),
                f"manufacturer '{manufacturer_id}'",
            )
            model = self.localized_name(car.get("Name"), f"car '{identifier}'")
            if manufacturer is None or model is None:
                raise CatalogError(f"Catalog validation failed: car '{identifier}' has no display name.")
            display_name = (
                model
                if model.casefold().startswith(f"{manufacturer} ".casefold())
                else f"{manufacturer} {model}"
            )
            catalog_cars.append(
                {
                    "id": identifier,
                    "manufacturer": manufacturer_id,
                    "name": model,
                    "displayName": display_name,
                    "class": {
                        "id": class_id,
                        "name": self.localized_name(car_class.get("Name"), f"class '{class_id}'"),
                    },
                    "group": {
                        "id": group_id,
                        "name": self.localized_name(group.get("Name"), f"group '{group_id}'"),
                    },
                }
            )

        catalog = {
            "schemaVersion": 1,
            "source": {
                "gameData": "ACRContent",
                "generatedBy": "tools/extract-acr-content.py",
            },
            "locations": catalog_locations,
            "countries": catalog_countries,
            "stages": catalog_stages,
            "cars": catalog_cars,
        }
        self.validate_catalog(catalog)
        return catalog

    def write_catalog(self) -> None:
        catalog = self.generate()
        self.output_path.parent.mkdir(parents=True, exist_ok=True)
        with self.output_path.open("w", encoding="utf-8", newline="\n") as output:
            json.dump(catalog, output, ensure_ascii=False, indent=2)
            output.write("\n")
        print(
            f"Validated and wrote {self.output_path} "
            f"({self.normalizations} whitespace normalization(s))"
        )


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--input-directory",
        type=Path,
        default=SCRIPT_ROOT.parent / "inputs" / "ACRContent",
        help="directory containing the FModel JSON exports",
    )
    parser.add_argument(
        "--output-path",
        type=Path,
        default=SCRIPT_ROOT.parent / "src" / "Content" / "acr-content.json",
        help="catalog JSON file to generate",
    )
    return parser.parse_args()


def main() -> int:
    arguments = parse_arguments()
    try:
        CatalogGenerator(arguments.input_directory, arguments.output_path).write_catalog()
    except (CatalogError, KeyError, OSError, json.JSONDecodeError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
