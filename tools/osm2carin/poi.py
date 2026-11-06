"""Navigation POI categories: OSM key=value -> category -> CARiN category code.

Only these become POIs on the disc; everything else (benches, bins, artwork...)
is counted as dropped.

CARiN category codes are the u16 at 0x06 record +0x0A and in the 0x0C section 3 city
ranges; block 0x07 section 0 lists the codes present on a disc, names empty (the unit
has its own names). OpenCarin issue #24 / docs/carindb/01-architecture.md 4.2, 4.4.2.
On CD-ID 5944 the list is exactly the 39 codes its 249,000 0x06 records use. Names:
issue #24 (read on a CNI1) where marked *, else from the POIs filed under the code on
CD-ID 5944 (carin-disc-findings.md "POI categories").
"""

# CARiN code -> name (CD-ID 5944 has all of these, and only these)
CODES = {
    11: 'auto services and dealers', 12: 'petrol stations*', 13: 'car rental*', 14: 'parking*',
    15: 'park and ride', 16: 'rest areas, service plazas*', 17: 'automobile clubs',
    20: 'tourist attractions', 21: 'hotels*', 22: 'restaurants*', 23: 'banks',
    24: 'community and civic centres', 25: 'libraries', 26: 'courts', 30: 'cash machines / banks',
    31: 'tourist information*', 32: 'museums*', 35: 'sports centres, stadiums*', 37: 'landmarks*',
    38: 'amusement and entertainment', 39: 'parks*', 40: 'convention and business facilities',
    41: 'hospitals*', 42: 'police', 43: 'city halls', 47: 'shopping*', 48: 'towns (city centres)*',
    50: 'golf*', 51: 'railway stations*', 52: 'airports*', 53: 'ferry terminals*',
    54: 'bus and coach stations*', 55: 'marinas', 56: 'schools, colleges and universities*',
    57: 'theatres, casinos and leisure*', 58: 'border crossings', 59: 'bmw dealers',
    60: 'auto services and dealers (second list, same POIs as 11)', 61: 'business and commercial',
}

CATEGORIES = {
    # motoring
    'amenity=fuel': 'petrol station', 'amenity=charging_station': 'ev charging',
    'amenity=parking': 'parking', 'amenity=parking_entrance': 'parking',
    'shop=car': 'car dealer', 'shop=car_repair': 'car repair', 'shop=tyres': 'car repair',
    'amenity=car_rental': 'car rental', 'amenity=car_wash': 'car wash',
    'highway=rest_area': 'rest area', 'highway=services': 'rest area',
    # travel
    'aeroway=aerodrome': 'airport', 'railway=station': 'railway station', 'railway=halt': 'railway station',
    'public_transport=station': 'railway station', 'amenity=bus_station': 'bus station',
    'amenity=ferry_terminal': 'ferry terminal',
    'tourism=hotel': 'hotel', 'tourism=motel': 'hotel', 'tourism=hostel': 'hotel',
    'tourism=guest_house': 'hotel', 'tourism=apartment': 'hotel',
    'tourism=camp_site': 'campsite', 'tourism=caravan_site': 'campsite',
    'amenity=restaurant': 'restaurant', 'amenity=fast_food': 'restaurant', 'amenity=food_court': 'restaurant',
    'amenity=cafe': 'cafe', 'amenity=bar': 'bar', 'amenity=pub': 'bar',
    # emergencies & services
    'amenity=hospital': 'hospital', 'amenity=clinic': 'doctor', 'amenity=doctors': 'doctor',
    'amenity=dentist': 'doctor', 'amenity=pharmacy': 'pharmacy', 'healthcare=hospital': 'hospital',
    'amenity=police': 'police', 'amenity=fire_station': 'fire station',
    'amenity=post_office': 'post office', 'amenity=bank': 'bank', 'amenity=atm': 'atm',
    'amenity=townhall': 'city hall', 'amenity=courthouse': 'court', 'office=government': 'government',
    'amenity=embassy': 'embassy', 'office=diplomatic': 'embassy',
    'amenity=library': 'library', 'amenity=university': 'university', 'amenity=college': 'university',
    'amenity=school': 'school',
    # leisure & sights
    'amenity=cinema': 'cinema', 'amenity=theatre': 'theatre', 'amenity=arts_centre': 'theatre',
    'amenity=casino': 'casino', 'amenity=nightclub': 'nightlife',
    'tourism=museum': 'museum', 'tourism=gallery': 'museum', 'tourism=attraction': 'sight',
    'tourism=viewpoint': 'sight', 'historic=monument': 'sight', 'historic=castle': 'sight',
    'tourism=zoo': 'zoo', 'tourism=theme_park': 'amusement park', 'leisure=water_park': 'amusement park',
    'tourism=information': 'tourist information',
    'leisure=golf_course': 'golf', 'leisure=stadium': 'stadium', 'leisure=sports_centre': 'sports centre',
    'leisure=ice_rink': 'sports centre', 'leisure=marina': 'marina', 'leisure=park': 'park',
    'amenity=place_of_worship': 'place of worship', 'amenity=community_centre': 'community centre',
    'amenity=conference_centre': 'exhibition centre', 'amenity=exhibition_centre': 'exhibition centre',
    # shopping
    'shop=supermarket': 'supermarket', 'shop=mall': 'shopping centre',
    'shop=department_store': 'shopping centre', 'shop=convenience': 'convenience store',
    'shop=hardware': 'diy store', 'shop=doityourself': 'diy store',
}

# our category -> CARiN code
CODE_OF = {
    'petrol station': 12, 'ev charging': 12, 'parking': 14, 'car dealer': 11, 'car repair': 11,
    'car rental': 13, 'car wash': 11, 'rest area': 16, 'airport': 52, 'railway station': 51,
    'bus station': 54, 'ferry terminal': 53, 'hotel': 21, 'campsite': 21, 'restaurant': 22, 'cafe': 22,
    'bar': 22, 'hospital': 41, 'doctor': 41, 'pharmacy': 47, 'police': 42, 'fire station': 24,
    'post office': 24, 'bank': 23, 'atm': 30, 'city hall': 43, 'court': 26, 'government': 43,
    'embassy': 61, 'library': 25, 'university': 56, 'school': 56, 'cinema': 57, 'theatre': 57,
    'casino': 57, 'nightlife': 57, 'museum': 32, 'sight': 37, 'zoo': 20, 'amusement park': 38,
    'tourist information': 31, 'golf': 50, 'stadium': 35, 'sports centre': 35, 'marina': 55,
    'park': 39, 'place of worship': 37, 'community centre': 24, 'exhibition centre': 40,
    'supermarket': 47, 'shopping centre': 47, 'convenience store': 47, 'diy store': 47,
}

# Categories kept even without a name (the unit lists them by category / brand).
UNNAMED_OK = {'petrol station', 'parking', 'ev charging', 'atm', 'rest area', 'hospital', 'police',
              'airport', 'railway station', 'car wash'}


def category(key, value, named, tourism_info=None):
    """-> category name or None (not a navigation POI)."""
    c = CATEGORIES.get(f'{key}={value}')
    if c is None:
        return None
    if c == 'tourist information' and tourism_info not in ('office', 'visitor_centre'):
        return None
    if not named and c not in UNNAMED_OK:
        return None
    return c
