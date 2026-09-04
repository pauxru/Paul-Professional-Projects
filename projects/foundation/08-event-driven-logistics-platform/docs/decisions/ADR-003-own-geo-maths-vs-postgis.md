# ADR-003 — Implement local geo maths and map production to PostGIS

## Status

Accepted.

## Context

The build host has no PostgreSQL/PostGIS. The project must run and test with no external infrastructure, yet geofence containment and corridor checks are central rather than cosmetic. Replacing them with mocks would avoid demonstrating the actual spatial mechanics.

## Options

1. Omit spatial behaviour or mock every result.
2. Depend on a remote spatial API/database.
3. Implement the required algorithms against latitude/longitude and use a grid candidate index locally.
4. Embed another native spatial engine.

## Decision

Use option 3:

- haversine distance on a spherical Earth radius of 6,371.0088 km;
- ray-casting polygon containment with an explicit inclusive edge/vertex test;
- local equirectangular point-to-segment distance for route corridors;
- circle/polygon bounding boxes;
- configurable latitude/longitude grid bucketing to reduce exact evaluations.

Random property tests prove the index returns the same containing set as brute force.

## Production PostGIS mapping

| Local model/operation | PostgreSQL/PostGIS mapping |
|---|---|
| Vehicle point (`lat`, `lon`) | `geography(Point, 4326)` |
| Polygon JSON | validated `geography(Polygon, 4326)` or geometry plus projection policy |
| Route polyline JSON | `geography(LineString, 4326)` |
| Haversine | `ST_Distance(point_a, point_b) / 1000` |
| Circle contains | `ST_DWithin(point, centre, radius_metres)` |
| Polygon contains incl. boundary | `ST_Covers(polygon::geometry, point::geometry)` |
| Route corridor | `ST_DWithin(point, route, corridor_metres)` |
| Bounding/grid candidates | GiST/SP-GiST index; optional H3/geohash partition column |

Create GiST indexes on vehicle/geofence/route geography columns. Store SRID 4326 explicitly and make distance units metres at the database boundary. Validate polygon rings and repair/reject invalid shapes before persistence.

## Consequences

- The repository is completely runnable and the maths is reviewable.
- Algorithmic edge cases are directly tested.
- The measured grid benchmark demonstrates why spatial candidate indexes matter.
- Local calculations have lower geodetic fidelity than PostGIS for long/complex shapes.

## Risks

- Anti-meridian crossing, polar cells and polygon holes are not handled.
- Equirectangular segment distance is intended for short urban corridors.
- Spherical haversine differs slightly from ellipsoidal geodesics.
- Local and PostGIS boundary semantics must remain aligned during migration.

## Alternatives

NetTopologySuite would be appropriate where package/native availability is assured, but hand-built algorithms are intentional here. A hosted geospatial API was rejected because it violates offline reproducibility and introduces cost/availability dependencies.
