"""Navigation POI categories: OSM key=value -> category.

Only these become POIs on the disc; everything else (benches, bins, artwork…)
is counted as dropped. The category names follow a classic in-car POI menu.
The mapping to the original disc's 0x06/0x10 category codes is Phase 2b
(they are not decoded yet).
"""

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
