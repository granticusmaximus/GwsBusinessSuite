#!/usr/bin/env python3
"""Builds the place tables World News uses to put stories on the Overwatch globe.

Inputs (download, then pass their paths):
  ne_50m_admin_0_countries.geojson   Natural Earth (public domain) - names in 26 languages, label points
  ne_110m_admin_0_countries.geojson  Natural Earth - simplified outlines for country shading
  cities15000.txt                    GeoNames (CC BY 4.0) - https://download.geonames.org/export/dump/

Outputs into src/GwsBusinessSuite.Infrastructure/WorldNewsPlaces/ (embedded in the build):
  countries.tsv   iso2, name, lat, lon, aliases (|)
  cities.tsv      name, iso2, lat, lon, population, aliases (|)
  countries.geojson   iso2 + name + outline, coordinates rounded to 0.01 degrees (point-in-country
                      checks, and Overwatch's country shading via /admin/api/world-news/countries.geojson)

Usage: scripts/build-world-news-places.py <ne50.geojson> <ne110.geojson> <cities15000.txt>
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "GwsBusinessSuite.Infrastructure", "WorldNewsPlaces")

# The names the app has always used (they match World News's edition countries).
CANONICAL = {"CN": "China", "US": "United States", "TR": "Turkey", "CZ": "Czechia", "KR": "South Korea", "KP": "North Korea",
             "RU": "Russia", "GB": "United Kingdom", "CD": "DR Congo", "CG": "Republic of the Congo",
             "CI": "Ivory Coast", "BS": "Bahamas", "GM": "Gambia", "TL": "East Timor", "SZ": "Eswatini",
             "MK": "North Macedonia", "VA": "Vatican City", "PS": "Palestine", "LA": "Laos", "VN": "Vietnam",
             "CV": "Cape Verde", "FM": "Micronesia", "TW": "Taiwan", "SY": "Syria", "IR": "Iran", "BN": "Brunei",
             "MD": "Moldova", "TZ": "Tanzania", "BO": "Bolivia", "VE": "Venezuela"}

# Short forms and demonyms Natural Earth doesn't carry. "American" is left out: "Latin American"
# and "South American" would land in the US.
EXTRA = {
    "US": ["U.S.", "US", "USA", "United States"], "GB": ["UK", "Britain", "British", "Northern Ireland", "England",
    "Scotland", "Scottish", "Wales", "Welsh"], "AE": ["UAE", "Emirati"],
    "CA": ["Canadian"], "AU": ["Australian"], "IN": ["Indian"], "NG": ["Nigerian"], "ZA": ["South African"],
    "KE": ["Kenyan"], "PH": ["Philippine", "Filipino"], "PK": ["Pakistani"], "FR": ["French"], "DE": ["German"],
    "IT": ["Italian"], "ES": ["Spanish"], "PT": ["Portuguese"], "NL": ["Dutch", "Holland"], "SE": ["Swedish"],
    "NO": ["Norwegian"], "PL": ["Polish"], "CZ": ["Czech Republic", "Czech"], "HU": ["Hungarian"],
    "RO": ["Romanian"], "GR": ["Greek"], "UA": ["Ukrainian"], "RU": ["Russian"], "TR": ["Turkish", "Türkiye"],
    "IL": ["Israeli"], "SA": ["Saudi"], "EG": ["Egyptian"], "BR": ["Brazilian"], "MX": ["Mexican"],
    "AR": ["Argentine", "Argentinian"], "CO": ["Colombian"], "JP": ["Japanese"], "KR": ["South Korean"],
    "KP": ["North Korean"], "CN": ["Chinese"], "TW": ["Taiwanese"], "ID": ["Indonesian"], "TH": ["Thai"],
    "VN": ["Vietnamese"], "IE": ["Irish"], "BE": ["Belgian"], "CH": ["Swiss"], "AT": ["Austrian"],
    "DK": ["Danish"], "FI": ["Finnish"], "IS": ["Icelandic"], "EE": ["Estonian"], "LV": ["Latvian"],
    "LT": ["Lithuanian"], "BY": ["Belarusian"], "MD": ["Moldovan"], "RS": ["Serbian"], "HR": ["Croatian"],
    "BA": ["Bosnian"], "SI": ["Slovenian"], "SK": ["Slovak"], "BG": ["Bulgarian"], "AL": ["Albanian"],
    "MK": ["Macedonian"], "XK": ["Kosovan", "Kosovar"], "ME": ["Montenegrin"], "CY": ["Cypriot"],
    "MT": ["Maltese"], "GE": ["Georgian"], "AM": ["Armenian"], "AZ": ["Azerbaijani"], "KZ": ["Kazakh"],
    "UZ": ["Uzbek"], "TM": ["Turkmen"], "KG": ["Kyrgyz"], "TJ": ["Tajik"], "AF": ["Afghan"], "IR": ["Iranian"],
    "IQ": ["Iraqi"], "SY": ["Syrian"], "LB": ["Lebanese"], "JO": ["Jordanian"], "PS": ["Palestinian"],
    "YE": ["Yemeni"], "OM": ["Omani"], "QA": ["Qatari"], "KW": ["Kuwaiti"], "BH": ["Bahraini"],
    "LY": ["Libyan"], "TN": ["Tunisian"], "DZ": ["Algerian"], "MA": ["Moroccan"], "SD": ["Sudanese"],
    "SS": ["South Sudanese"], "ET": ["Ethiopian"], "ER": ["Eritrean"], "SO": ["Somali"], "DJ": ["Djiboutian"],
    "UG": ["Ugandan"], "RW": ["Rwandan"], "BI": ["Burundian"], "TZ": ["Tanzanian"], 
    "AO": ["Angolan"], "ZM": ["Zambian"], "ZW": ["Zimbabwean"], "MZ": ["Mozambican"], "MW": ["Malawian"],
    "MG": ["Malagasy"], "NA": ["Namibian"], "BW": ["Botswanan"], "GH": ["Ghanaian"], "CI": ["Ivorian",
    "Côte d'Ivoire"], "SN": ["Senegalese"], "ML": ["Malian"], "BF": ["Burkinabe", "Burkina"], "NE": ["Nigerien"],
    "TD": ["Chadian"], "CM": ["Cameroonian"], "CF": ["Central African Republic"], "GA": ["Gabonese"],
    "SL": ["Sierra Leonean"], "LR": ["Liberian"], "BD": ["Bangladeshi"],
    "LK": ["Sri Lankan"], "NP": ["Nepali", "Nepalese"], "BT": ["Bhutanese"], "MM": ["Myanmar", "Burmese", "Burma"],
    "KH": ["Cambodian"], "LA": ["Laotian"], "MY": ["Malaysian"], "SG": ["Singaporean"], "MN": ["Mongolian"],
    "NZ": ["New Zealander"], "PG": ["Papua New Guinean"], "FJ": ["Fijian"], "CU": ["Cuban"],
    "HT": ["Haitian"], "DO": ["Dominican Republic"], "JM": ["Jamaican"], "PR": ["Puerto Rican"],
    "GT": ["Guatemalan"], "HN": ["Honduran"], "SV": ["Salvadoran"], "NI": ["Nicaraguan"], "CR": ["Costa Rican"],
    "PA": ["Panamanian"], "VE": ["Venezuelan"], "EC": ["Ecuadorian"], "PE": ["Peruvian"], "BO": ["Bolivian"],
    "PY": ["Paraguayan"], "UY": ["Uruguayan"], "CL": ["Chilean"], "GL": ["Greenlandic"], "VA": ["Vatican"],
}

# Names that are too often something else to place a story on their own (a US state, a person,
# Iceland's "Island", Grenada's Spanish "Granada").
# Their capitals and demonyms still work.
AMBIGUOUS = {"Island", "Granada", "Grenade", "Chili", "Georgia", "Jordan", "Chad", "Congo", "Guinea", "Dominica", "Jersey", "Niger"}

CJK = re.compile(r"[぀-ヿ㐀-鿿가-힯฀-๿]")


def iso_of(p):
    # ISO_A2_EH fills Natural Earth's "-99"s (France, Norway) and gives Taiwan "TW", not "CN-TW".
    iso = p["ISO_A2_EH"] if p["ISO_A2_EH"] not in ("-99", None) else p["ISO_A2"]
    return None if iso in ("-99", None) else iso


def keep_alias(alias):
    alias = alias.strip()
    if not alias:
        return None
    if CJK.search(alias):
        return alias
    return alias if len(alias) >= 4 or alias in {"US", "UK", "UAE", "USA", "U.S."} else None


def countries(ne50):
    data = json.load(open(ne50, encoding="utf-8"))
    rows, owners, biggest = [], {}, {}
    for feature in data["features"]:
        iso = iso_of(feature["properties"])
        biggest[iso] = max(biggest.get(iso, 0), feature["properties"]["POP_EST"])
    for feature in data["features"]:
        p = feature["properties"]
        iso = iso_of(p)
        # Small territories share their country's code (Ashmore and Cartier Islands is "AU").
        if not iso or p["POP_EST"] < biggest[iso]:
            continue
        name = CANONICAL.get(iso, p["NAME_EN"] or p["NAME"])
        names = {name, p["NAME"], p["NAME_EN"], p["ADMIN"], p["NAME_LONG"]}
        names |= {p[k] for k in p if k.startswith("NAME_") and k not in ("NAME_LEN", "NAME_SORT") and isinstance(p[k], str)}
        names |= set(EXTRA.get(iso, []))
        aliases = {a for a in (keep_alias(n) for n in names if isinstance(n, str)) if a and a not in AMBIGUOUS}
        rows.append([iso, name, round(p["LABEL_Y"], 2), round(p["LABEL_X"], 2), aliases])
        for alias in aliases:
            owners.setdefault(alias, set()).add(iso)
    # An alias two countries share ("Congo") names neither.
    shared = {alias for alias, isos in owners.items() if len(isos) > 1}
    return [[iso, name, lat, lon, sorted(a - shared, key=lambda s: (-len(s), s))] for iso, name, lat, lon, a in rows]


# City names that are everyday words or first names in a Title Case English headline.
CITY_STOP = set("""Most Nice Split Reading Bath Mobile Orange Sale Independence Temple Victoria Hope Union Concord
Corona Bend Troy Gary Irving Eugene Lawrence Warren Kent Lincoln Jackson Charlotte Florence Columbia Arlington
Mission Sandy Murray Vista Deal Delta Mesa Male Nancy Angers Tours Aurora Surprise Allen Norman Chandler Gilbert
Savannah Hamilton Wellington Darwin Carmel Fishers Clinton Midland Phoenix Buffalo March Salem Batman Marino
Paradise Pueblo Plano Lakewood Glendale Elgin Lafayette Columbus Augusta Odessa Kingston Richmond Springfield
Georgetown Jacksonville Franklin Henderson Lewisville Pearland League Miramar Sunrise Palm
Tyler Waco Davie Weston Hollywood Beverly Rochester Madison Albany Cambridge Oxford Cleveland Dayton Akron
Brandon Burlington Durham Newcastle Bristol Stockton Fort Port Mansfield Carson Lancaster Ontario Pomona Kenner
Bloomington Champaign Oshawa Barrie Guelph Waterloo Victorville Thornton Westminster Centennial Boulder Greeley
Fairfield Vallejo Ventura Oxnard Downey Fullerton Anaheim Irvine Mercedes Florida
Leon Cork Derby Luton Reading Chester Lincoln Bolton Sale Wigan Gloucester Exeter
Plymouth Hastings Worthing Crawley Basildon Maidstone Gillingham Poole Bournemouth Swindon Telford Dudley Bury
Rotherham Barnsley Doncaster Halifax Huddersfield Wakefield Harrogate Woking Slough Watford Stevenage
Bedford Ipswich Norwich Peterborough Colchester Chelmsford Southend Northampton Solihull Walsall
Wolverhampton Coventry Oldham Rochdale Stockport Warrington Blackpool Preston Blackburn Burnley Middlesbrough
Darlington Hartlepool Gateshead Sunderland Carlisle Dundee Perth Paisley Livingston Hamilton Bangor Mesquite
Garland Frisco Denton Carrollton Richardson Killeen Abilene Amarillo Lubbock Beaumont Tyler Bryan
Edison Paterson Clifton Passaic Trenton Camden Elizabeth Bayonne Hoboken Union Linden Kearny Vineland
Brockton Lowell Lynn Quincy Newton Cambridge Worcester Haverhill Malden Medford Waltham Taunton 
Laval Longueuil Gatineau Sherbrooke Saguenay Levis Kelowna Abbotsford Coquitlam Richmond Burnaby Surrey

Pau Caen Orleans Limoges Perpignan Besancon Mulhouse Rouen Dijon Nimes Avignon Poitiers Calais Beziers
Lorient Valence Cholet Niort Chambery Annecy Bayonne Quimper Troyes Colmar Belfort Blois Bourges Vannes
""".split())


# Lower-case English words (macOS/Linux word list) - optional; without it fewer names are dropped.
ENGLISH_WORDS = {w.strip() for w in open("/usr/share/dict/words", encoding="utf-8") if w.strip().islower()} \
    if os.path.exists("/usr/share/dict/words") else set()


def cities(path, country_aliases):
    rows, names = [], {}
    for line in open(path, encoding="utf-8"):
        f = line.rstrip("\n").split("\t")
        population = int(f[14] or 0)
        if population < 50000 or f[6] != "P":
            continue
        name, ascii_name, iso = f[1], f[2], f[8]
        aliases = {name, ascii_name}
        if population >= 500000:
            for alt in f[3].split(","):
                # Latin-script spellings only (Seville, Sevilha, Séville); transliterations with
                # apostrophes are dictionary forms no headline uses.
                if len(alt) >= 4 and alt[0].isupper() and alt.count(" ") <= 2 \
                        and all(c in " -" or (c.isalpha() and ord(c) < 0x250) for c in alt):
                    aliases.add(alt)
        # "Soul" (Seoul) or "Wedding" (a Berlin district) would catch ordinary Title Case words.
        # A city's own name survives that unless it's small ("Providence" yes, "Mobile" no).
        aliases = {a for a in aliases if a not in CITY_STOP and a not in country_aliases and len(a) >= 4
                   and not (a.lower() in ENGLISH_WORDS and (a not in (name, ascii_name) or population < 200000))}
        if not aliases:
            continue
        rows.append([name, iso, round(float(f[4]), 3), round(float(f[5]), 3), population, aliases])
    return [[n, iso, lat, lon, pop, sorted(a, key=lambda s: (-len(s), s))] for n, iso, lat, lon, pop, a in rows]


def outlines(ne110, isos):
    data = json.load(open(ne110, encoding="utf-8"))
    out = []
    for feature in data["features"]:
        p = feature["properties"]
        iso = iso_of(p)
        if iso not in isos:
            continue
        geometry = feature["geometry"]

        def rnd(c):
            return [rnd(x) for x in c] if isinstance(c[0], list) else [round(c[0], 2), round(c[1], 2)]
        out.append({"type": "Feature", "properties": {"iso": iso, "name": isos[iso]},
                    "geometry": {"type": geometry["type"], "coordinates": rnd(geometry["coordinates"])}})
    return {"type": "FeatureCollection", "features": out}


def main(ne50, ne110, cities_path):
    os.makedirs(OUT, exist_ok=True)
    country_rows = countries(ne50)
    all_country_aliases = {a for row in country_rows for a in row[4]} | AMBIGUOUS
    with open(os.path.join(OUT, "countries.tsv"), "w", encoding="utf-8") as f:
        f.write("# Natural Earth 1:50m admin-0 (public domain). Built by scripts/build-world-news-places.py\n")
        for iso, name, lat, lon, aliases in sorted(country_rows):
            f.write(f"{iso}\t{name}\t{lat}\t{lon}\t{'|'.join(aliases)}\n")
    city_rows = cities(cities_path, all_country_aliases)
    with open(os.path.join(OUT, "cities.tsv"), "w", encoding="utf-8") as f:
        f.write("# GeoNames cities15000, 50k+ people (CC BY 4.0, geonames.org). Built by scripts/build-world-news-places.py\n")
        for name, iso, lat, lon, pop, aliases in sorted(city_rows, key=lambda r: -r[4]):
            f.write(f"{name}\t{iso}\t{lat}\t{lon}\t{pop}\t{'|'.join(aliases)}\n")
    names = {row[0]: row[1] for row in country_rows}
    with open(os.path.join(OUT, "countries.geojson"), "w", encoding="utf-8") as f:
        json.dump(outlines(ne110, names), f, ensure_ascii=False, separators=(",", ":"))
    print(f"{len(country_rows)} countries, {len(city_rows)} cities")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])
