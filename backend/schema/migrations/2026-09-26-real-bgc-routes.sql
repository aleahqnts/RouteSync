-- Real BGC Bus routes on the fleet map, and how accurate each GPS reading was.
--
-- Background
--
-- The two routes were entered by hand as a line of points and a list of stops. The line cut
-- across blocks in places and some stops sat away from where the buses stop, and the second
-- route, South Line, was drawn to Arca South, a service suspended since 1 April 2026. The
-- fleet map is about to snap each bus onto its route line, which only works if that line is
-- the road the bus actually drives.
--
-- Source
--
-- Both routes come from OpenStreetMap, the map the dashboard already draws its tiles from,
-- so the line sits exactly on the roads underneath it.
--
--   Route 01, North Express   BGC Bus NX: North Express, relation 11192397. A closed loop
--                             of 407 points: out of the EDSA-Ayala terminal by McKinley
--                             Road, round northern BGC and back. OSM stores the
--                             terminal end of this relation out of order, so the line was
--                             rebuilt by following the shared road nodes, and each one-way
--                             street was checked for direction. Two short pieces of 38th Street
--                             are the far carriageway of a divided road, some 15 m from the
--                             lane the bus uses, which the 50 m snap radius absorbs.
--                             Stops, from OSM: EDSA-Ayala Terminal, NutriAsia, HSBC, Lexus Manila, Avida Towers Verte, Uptown Parade, The Globe Tower, The Fort Strip.
--
--   Route 02, Central         BGC Bus C: Central Route, relation 11192401, 296 points.
--                             OSM's own stop list for it is out of date, so the stops are the
--                             published list, placed at OSM's stop positions, every one within
--                             10 m of the line and in travel order: Market! Market!, NutriAsia, The Fort Station, Net One, Bonifacio Stopover, Crescent Park West, The Globe Tower, One Parkade, University Parkway.
--
-- Route data (c) OpenStreetMap contributors, available under the Open Database License. The
-- map already carries the attribution the licence asks for.
--
-- What changes
--
-- Route 02 is renamed from South Line to Central. Nothing in the three apps selects a route
-- by its name; trips, rosters and reports all hold the route id, which does not change. The
-- audit log and messages already sent keep the old name, as they should.
--
-- A stop may now carry "terminal": true, marking where the route's parked buses are drawn.
-- The dashboard reads name, lat and lng from each stop and ignores anything else, so this
-- is safe to run before the build that reads the flag. Until that build is deployed, Route
-- 02's parked buses are still drawn at Arca South.
--
-- origin and destination follow the new routes. Nothing displays them.
--
-- telemetry_data gains accuracy, the radius in metres Android reports for each fix. A
-- reading from a build that does not send it leaves it null, which the fleet map treats as
-- usable. app_driver already holds insert on the table, which covers the new column.
--
-- Backup
--
-- The two route rows as they stood are copied to routes_before_osm first, and the
-- rollback restores them from there. The migration refuses to run a second time, so the
-- backup is never overwritten with its own result.
--
-- Run the whole file in the Supabase SQL editor with nothing highlighted.

begin;

create table if not exists public.routes_before_osm (
  route_id       integer                  not null,
  route_name     character varying(100)   not null,
  origin         character varying(100)   not null,
  destination    character varying(100)   not null,
  waypoints_json text,
  stops_json     text,
  updated_at     timestamp with time zone,
  saved_at       timestamp with time zone not null default now(),
  constraint pk_routes_before_osm primary key (route_id)
);

alter table public.routes_before_osm enable row level security;
revoke all on table public.routes_before_osm from anon, authenticated;
grant all on table public.routes_before_osm to service_role;

comment on table public.routes_before_osm is
  'The route rows as they stood before 2026-09-26-real-bgc-routes.sql replaced them with OpenStreetMap data. Read by its rollback.';

do $routes$
declare
  v_north  integer;
  v_second integer;

  v_nx_line text := $nx_line$[
{"lat":14.549222,"lng":121.029095},{"lat":14.549347,"lng":121.028998},{"lat":14.549476,"lng":121.028874},{"lat":14.54951,"lng":121.028849},{"lat":14.54956,"lng":121.028841},
{"lat":14.549611,"lng":121.02885},{"lat":14.550071,"lng":121.029408},{"lat":14.549886,"lng":121.029583},{"lat":14.549771,"lng":121.029692},{"lat":14.549736,"lng":121.029725},
{"lat":14.549705,"lng":121.02976},{"lat":14.549653,"lng":121.029825},{"lat":14.549633,"lng":121.029854},{"lat":14.549592,"lng":121.029921},{"lat":14.549553,"lng":121.029988},
{"lat":14.549494,"lng":121.030105},{"lat":14.54948,"lng":121.030164},{"lat":14.549465,"lng":121.030225},{"lat":14.549452,"lng":121.030354},{"lat":14.549247,"lng":121.030823},
{"lat":14.548453,"lng":121.032638},{"lat":14.548232,"lng":121.033164},{"lat":14.548138,"lng":121.033388},{"lat":14.548126,"lng":121.033417},{"lat":14.548111,"lng":121.033453},
{"lat":14.547771,"lng":121.034257},{"lat":14.547719,"lng":121.034379},{"lat":14.547566,"lng":121.034777},{"lat":14.547279,"lng":121.035522},{"lat":14.547238,"lng":121.035665},
{"lat":14.547216,"lng":121.035831},{"lat":14.547211,"lng":121.035942},{"lat":14.547302,"lng":121.036667},{"lat":14.547324,"lng":121.036838},{"lat":14.547334,"lng":121.036921},
{"lat":14.547479,"lng":121.038116},{"lat":14.547564,"lng":121.038753},{"lat":14.547576,"lng":121.03884},{"lat":14.547586,"lng":121.038929},{"lat":14.547603,"lng":121.039069},
{"lat":14.547606,"lng":121.039105},{"lat":14.547631,"lng":121.039295},{"lat":14.547633,"lng":121.039306},{"lat":14.547658,"lng":121.03949},{"lat":14.54771,"lng":121.039852},
{"lat":14.547724,"lng":121.040005},{"lat":14.547729,"lng":121.040113},{"lat":14.547723,"lng":121.040241},{"lat":14.547709,"lng":121.040335},{"lat":14.54767,"lng":121.040481},
{"lat":14.547615,"lng":121.040638},{"lat":14.547529,"lng":121.040782},{"lat":14.547437,"lng":121.040896},{"lat":14.547266,"lng":121.041069},{"lat":14.547143,"lng":121.041172},
{"lat":14.546742,"lng":121.041464},{"lat":14.546249,"lng":121.041812},{"lat":14.54605,"lng":121.041953},{"lat":14.545908,"lng":121.042073},{"lat":14.545848,"lng":121.042136},
{"lat":14.545828,"lng":121.042158},{"lat":14.545722,"lng":121.042278},{"lat":14.54564,"lng":121.042385},{"lat":14.545502,"lng":121.0426},{"lat":14.545401,"lng":121.042799},
{"lat":14.544881,"lng":121.043859},{"lat":14.544825,"lng":121.044006},{"lat":14.544815,"lng":121.044189},{"lat":14.54482,"lng":121.044347},{"lat":14.544866,"lng":121.044508},
{"lat":14.544946,"lng":121.04472},{"lat":14.544965,"lng":121.044776},{"lat":14.545025,"lng":121.044933},{"lat":14.545048,"lng":121.045028},{"lat":14.545064,"lng":121.045181},
{"lat":14.545078,"lng":121.045373},{"lat":14.545083,"lng":121.045436},{"lat":14.545065,"lng":121.045487},{"lat":14.545064,"lng":121.045694},{"lat":14.545077,"lng":121.045795},
{"lat":14.545195,"lng":121.045791},{"lat":14.545274,"lng":121.045792},{"lat":14.54543,"lng":121.045795},{"lat":14.545585,"lng":121.04581},{"lat":14.545682,"lng":121.045826},
{"lat":14.545807,"lng":121.045854},{"lat":14.545829,"lng":121.04586},{"lat":14.545946,"lng":121.045892},{"lat":14.546505,"lng":121.046083},{"lat":14.547133,"lng":121.046295},
{"lat":14.547235,"lng":121.046328},{"lat":14.547336,"lng":121.046361},{"lat":14.547452,"lng":121.0464},{"lat":14.548186,"lng":121.046648},{"lat":14.548911,"lng":121.046912},
{"lat":14.548967,"lng":121.046932},{"lat":14.54905,"lng":121.046961},{"lat":14.549127,"lng":121.046989},{"lat":14.550666,"lng":121.047516},{"lat":14.550753,"lng":121.047547},
{"lat":14.550733,"lng":121.04761},{"lat":14.550627,"lng":121.047938},{"lat":14.550598,"lng":121.048028},{"lat":14.550579,"lng":121.04808},{"lat":14.550543,"lng":121.048144},
{"lat":14.550537,"lng":121.048156},{"lat":14.550413,"lng":121.048329},{"lat":14.550375,"lng":121.048376},{"lat":14.550336,"lng":121.048429},{"lat":14.550226,"lng":121.04858},
{"lat":14.550186,"lng":121.048635},{"lat":14.550146,"lng":121.048717},{"lat":14.55003,"lng":121.049069},{"lat":14.550014,"lng":121.049117},{"lat":14.549986,"lng":121.049204},
{"lat":14.549956,"lng":121.049293},{"lat":14.549863,"lng":121.049597},{"lat":14.549727,"lng":121.050021},{"lat":14.549694,"lng":121.050123},{"lat":14.549452,"lng":121.050885},
{"lat":14.549423,"lng":121.050973},{"lat":14.549507,"lng":121.051},{"lat":14.549933,"lng":121.051143},{"lat":14.549983,"lng":121.05116},{"lat":14.550045,"lng":121.051181},
{"lat":14.550304,"lng":121.051268},{"lat":14.550349,"lng":121.051283},{"lat":14.55065,"lng":121.051385},{"lat":14.550958,"lng":121.05149},{"lat":14.55101,"lng":121.051507},
{"lat":14.551056,"lng":121.051523},{"lat":14.551479,"lng":121.051664},{"lat":14.551567,"lng":121.051694},{"lat":14.551596,"lng":121.051604},{"lat":14.551715,"lng":121.051232},
{"lat":14.551832,"lng":121.050866},{"lat":14.551867,"lng":121.050756},{"lat":14.551979,"lng":121.050405},{"lat":14.552103,"lng":121.050017},{"lat":14.552133,"lng":121.049926},
{"lat":14.55216,"lng":121.049843},{"lat":14.552267,"lng":121.049492},{"lat":14.552276,"lng":121.049453},{"lat":14.552284,"lng":121.049403},{"lat":14.552285,"lng":121.049358},
{"lat":14.552287,"lng":121.049277},{"lat":14.552287,"lng":121.049097},{"lat":14.552285,"lng":121.048859},{"lat":14.552288,"lng":121.048817},{"lat":14.552298,"lng":121.048726},
{"lat":14.552308,"lng":121.048667},{"lat":14.55233,"lng":121.048578},{"lat":14.552373,"lng":121.048422},{"lat":14.552448,"lng":121.048184},{"lat":14.552465,"lng":121.04813},
{"lat":14.552571,"lng":121.048164},{"lat":14.553066,"lng":121.048331},{"lat":14.553519,"lng":121.048484},{"lat":14.553592,"lng":121.048508},{"lat":14.554049,"lng":121.04865},
{"lat":14.554079,"lng":121.048658},{"lat":14.554162,"lng":121.04867},{"lat":14.554266,"lng":121.04867},{"lat":14.554332,"lng":121.048693},{"lat":14.554704,"lng":121.048813},
{"lat":14.554818,"lng":121.048852},{"lat":14.554867,"lng":121.048868},{"lat":14.554998,"lng":121.048912},{"lat":14.555218,"lng":121.048986},{"lat":14.555367,"lng":121.049036},
{"lat":14.555771,"lng":121.049164},{"lat":14.555847,"lng":121.049188},{"lat":14.555929,"lng":121.049218},{"lat":14.555909,"lng":121.049294},{"lat":14.555831,"lng":121.04952},
{"lat":14.555759,"lng":121.04975},{"lat":14.55575,"lng":121.049785},{"lat":14.555729,"lng":121.049843},{"lat":14.555692,"lng":121.04996},{"lat":14.555426,"lng":121.050796},
{"lat":14.55539,"lng":121.050911},{"lat":14.55536,"lng":121.051005},{"lat":14.55533,"lng":121.051098},{"lat":14.555285,"lng":121.051243},{"lat":14.555274,"lng":121.051275},
{"lat":14.555229,"lng":121.051411},{"lat":14.555108,"lng":121.051783},{"lat":14.555081,"lng":121.051873},{"lat":14.55506,"lng":121.051934},{"lat":14.555044,"lng":121.051987},
{"lat":14.555017,"lng":121.05207},{"lat":14.554937,"lng":121.052324},{"lat":14.554821,"lng":121.052693},{"lat":14.554794,"lng":121.052777},{"lat":14.554881,"lng":121.052807},
{"lat":14.555243,"lng":121.05293},{"lat":14.55563,"lng":121.053062},{"lat":14.555717,"lng":121.053091},{"lat":14.555802,"lng":121.05312},{"lat":14.556014,"lng":121.053192},
{"lat":14.556353,"lng":121.053308},{"lat":14.556444,"lng":121.05334},{"lat":14.556511,"lng":121.053362},{"lat":14.556637,"lng":121.053402},{"lat":14.556962,"lng":121.053514},
{"lat":14.557189,"lng":121.053591},{"lat":14.557275,"lng":121.05362},{"lat":14.557362,"lng":121.053648},{"lat":14.557614,"lng":121.053731},{"lat":14.558352,"lng":121.053978},
{"lat":14.558933,"lng":121.054173},{"lat":14.559046,"lng":121.054212},{"lat":14.559002,"lng":121.05431},{"lat":14.558944,"lng":121.054498},{"lat":14.558851,"lng":121.054791},
{"lat":14.558778,"lng":121.055024},{"lat":14.55873,"lng":121.055096},{"lat":14.558704,"lng":121.055177},{"lat":14.558586,"lng":121.055547},{"lat":14.558467,"lng":121.055933},
{"lat":14.558446,"lng":121.055997},{"lat":14.558323,"lng":121.055957},{"lat":14.55808,"lng":121.055878},{"lat":14.557902,"lng":121.05582},{"lat":14.557725,"lng":121.055761},
{"lat":14.55755,"lng":121.055704},{"lat":14.557241,"lng":121.055602},{"lat":14.556968,"lng":121.055512},{"lat":14.556771,"lng":121.055447},{"lat":14.556696,"lng":121.055423},
{"lat":14.556603,"lng":121.055392},{"lat":14.556532,"lng":121.055368},{"lat":14.556286,"lng":121.055287},{"lat":14.5562,"lng":121.055259},{"lat":14.555993,"lng":121.055191},
{"lat":14.55573,"lng":121.055104},{"lat":14.55569,"lng":121.055091},{"lat":14.555501,"lng":121.055029},{"lat":14.555345,"lng":121.054977},{"lat":14.555201,"lng":121.05493},
{"lat":14.555162,"lng":121.054916},{"lat":14.555013,"lng":121.054865},{"lat":14.55484,"lng":121.054805},{"lat":14.554777,"lng":121.054782},{"lat":14.554728,"lng":121.054766},
{"lat":14.554495,"lng":121.054685},{"lat":14.5543,"lng":121.054618},{"lat":14.554228,"lng":121.054593},{"lat":14.554252,"lng":121.054519},{"lat":14.554349,"lng":121.054206},
{"lat":14.554394,"lng":121.054061},{"lat":14.554414,"lng":121.053997},{"lat":14.554431,"lng":121.053942},{"lat":14.554582,"lng":121.053459},{"lat":14.554641,"lng":121.053267},
{"lat":14.554767,"lng":121.052864},{"lat":14.554794,"lng":121.052777},{"lat":14.554821,"lng":121.052693},{"lat":14.554937,"lng":121.052324},{"lat":14.555017,"lng":121.05207},
{"lat":14.555044,"lng":121.051987},{"lat":14.55506,"lng":121.051934},{"lat":14.555081,"lng":121.051873},{"lat":14.55502,"lng":121.051787},{"lat":14.554514,"lng":121.051606},
{"lat":14.55398,"lng":121.051426},{"lat":14.553935,"lng":121.051391},{"lat":14.553923,"lng":121.051352},{"lat":14.553921,"lng":121.051305},{"lat":14.553932,"lng":121.051239},
{"lat":14.553958,"lng":121.051126},{"lat":14.553977,"lng":121.051029},{"lat":14.553999,"lng":121.050894},{"lat":14.55401,"lng":121.050777},{"lat":14.554016,"lng":121.050569},
{"lat":14.553748,"lng":121.050476},{"lat":14.553689,"lng":121.050456},{"lat":14.5536,"lng":121.050427},{"lat":14.55353,"lng":121.050402},{"lat":14.553105,"lng":121.050254},
{"lat":14.552869,"lng":121.050174},{"lat":14.552683,"lng":121.050111},{"lat":14.552218,"lng":121.049955},{"lat":14.552133,"lng":121.049926},{"lat":14.552045,"lng":121.049896},
{"lat":14.551627,"lng":121.049754},{"lat":14.551581,"lng":121.04974},{"lat":14.551531,"lng":121.049723},{"lat":14.551243,"lng":121.049626},{"lat":14.55122,"lng":121.049618},
{"lat":14.551161,"lng":121.0496},{"lat":14.551035,"lng":121.049556},{"lat":14.550923,"lng":121.049517},{"lat":14.550602,"lng":121.049409},{"lat":14.550545,"lng":121.049391},
{"lat":14.550495,"lng":121.049374},{"lat":14.550073,"lng":121.049234},{"lat":14.549986,"lng":121.049204},{"lat":14.549903,"lng":121.049175},{"lat":14.549502,"lng":121.049038},
{"lat":14.549432,"lng":121.049015},{"lat":14.549361,"lng":121.048991},{"lat":14.549189,"lng":121.048931},{"lat":14.548833,"lng":121.048811},{"lat":14.548616,"lng":121.048738},
{"lat":14.54859,"lng":121.048729},{"lat":14.548514,"lng":121.048705},{"lat":14.548534,"lng":121.048644},{"lat":14.548541,"lng":121.048621},{"lat":14.548544,"lng":121.048609},
{"lat":14.54866,"lng":121.048235},{"lat":14.548905,"lng":121.047438},{"lat":14.548967,"lng":121.047242},{"lat":14.549033,"lng":121.04702},{"lat":14.54905,"lng":121.046961},
{"lat":14.549021,"lng":121.046847},{"lat":14.548958,"lng":121.046825},{"lat":14.548942,"lng":121.046819},{"lat":14.547501,"lng":121.046314},{"lat":14.547362,"lng":121.046268},
{"lat":14.547223,"lng":121.046224},{"lat":14.54653,"lng":121.045995},{"lat":14.545969,"lng":121.045813},{"lat":14.545789,"lng":121.04576},{"lat":14.545714,"lng":121.045741},
{"lat":14.545593,"lng":121.04572},{"lat":14.545471,"lng":121.045709},{"lat":14.545252,"lng":121.045698},{"lat":14.545175,"lng":121.045698},{"lat":14.545134,"lng":121.045556},
{"lat":14.545112,"lng":121.045481},{"lat":14.545083,"lng":121.045436},{"lat":14.545078,"lng":121.045373},{"lat":14.545064,"lng":121.045181},{"lat":14.545048,"lng":121.045028},
{"lat":14.545025,"lng":121.044933},{"lat":14.544965,"lng":121.044776},{"lat":14.544946,"lng":121.04472},{"lat":14.544866,"lng":121.044508},{"lat":14.54482,"lng":121.044347},
{"lat":14.544815,"lng":121.044189},{"lat":14.544825,"lng":121.044006},{"lat":14.544881,"lng":121.043859},{"lat":14.545401,"lng":121.042799},{"lat":14.545502,"lng":121.0426},
{"lat":14.54564,"lng":121.042385},{"lat":14.545722,"lng":121.042278},{"lat":14.545828,"lng":121.042158},{"lat":14.545848,"lng":121.042136},{"lat":14.545908,"lng":121.042073},
{"lat":14.54605,"lng":121.041953},{"lat":14.546249,"lng":121.041812},{"lat":14.546742,"lng":121.041464},{"lat":14.547143,"lng":121.041172},{"lat":14.547266,"lng":121.041069},
{"lat":14.547437,"lng":121.040896},{"lat":14.547529,"lng":121.040782},{"lat":14.547615,"lng":121.040638},{"lat":14.54767,"lng":121.040481},{"lat":14.547709,"lng":121.040335},
{"lat":14.547723,"lng":121.040241},{"lat":14.547729,"lng":121.040113},{"lat":14.547724,"lng":121.040005},{"lat":14.54771,"lng":121.039852},{"lat":14.547658,"lng":121.03949},
{"lat":14.547633,"lng":121.039306},{"lat":14.547631,"lng":121.039295},{"lat":14.547606,"lng":121.039105},{"lat":14.547603,"lng":121.039069},{"lat":14.547586,"lng":121.038929},
{"lat":14.547576,"lng":121.03884},{"lat":14.547564,"lng":121.038753},{"lat":14.547479,"lng":121.038116},{"lat":14.547334,"lng":121.036921},{"lat":14.547324,"lng":121.036838},
{"lat":14.547302,"lng":121.036667},{"lat":14.547211,"lng":121.035942},{"lat":14.547216,"lng":121.035831},{"lat":14.547238,"lng":121.035665},{"lat":14.547279,"lng":121.035522},
{"lat":14.547566,"lng":121.034777},{"lat":14.547719,"lng":121.034379},{"lat":14.547771,"lng":121.034257},{"lat":14.548111,"lng":121.033453},{"lat":14.548126,"lng":121.033417},
{"lat":14.548138,"lng":121.033388},{"lat":14.548232,"lng":121.033164},{"lat":14.548453,"lng":121.032638},{"lat":14.549247,"lng":121.030823},{"lat":14.549452,"lng":121.030354},
{"lat":14.549523,"lng":121.030254},{"lat":14.54955,"lng":121.030199},{"lat":14.549577,"lng":121.030145},{"lat":14.549494,"lng":121.030105},{"lat":14.549424,"lng":121.030071},
{"lat":14.549307,"lng":121.030002},{"lat":14.549219,"lng":121.029925},{"lat":14.54917,"lng":121.029871},{"lat":14.549122,"lng":121.029798},{"lat":14.54909,"lng":121.029724},
{"lat":14.549071,"lng":121.029641},{"lat":14.549058,"lng":121.029549},{"lat":14.549062,"lng":121.029452},{"lat":14.549086,"lng":121.029365},{"lat":14.549115,"lng":121.029274},
{"lat":14.549175,"lng":121.029158},{"lat":14.549222,"lng":121.029095}
]$nx_line$;

  v_nx_stops text := $nx_stops$[
{"name":"EDSA-Ayala Terminal","lat":14.549272,"lng":121.029103,"terminal":true},
{"name":"NutriAsia","lat":14.551715,"lng":121.051232},
{"name":"HSBC","lat":14.553519,"lng":121.048484},
{"name":"Lexus Manila","lat":14.555274,"lng":121.051275},
{"name":"Avida Towers Verte","lat":14.555243,"lng":121.05293},
{"name":"Uptown Parade","lat":14.558352,"lng":121.053978},
{"name":"The Globe Tower","lat":14.552869,"lng":121.050174},
{"name":"The Fort Strip","lat":14.548967,"lng":121.047242}
]$nx_stops$;

  v_c_line text := $c_line$[
{"lat":14.549482,"lng":121.056767},{"lat":14.549439,"lng":121.056692},{"lat":14.549155,"lng":121.056545},{"lat":14.54902,"lng":121.056478},{"lat":14.548854,"lng":121.056425},
{"lat":14.548678,"lng":121.056457},{"lat":14.548183,"lng":121.056383},{"lat":14.548027,"lng":121.05635},{"lat":14.547865,"lng":121.056318},{"lat":14.547748,"lng":121.056297},
{"lat":14.547605,"lng":121.056266},{"lat":14.547476,"lng":121.056229},{"lat":14.547367,"lng":121.056177},{"lat":14.547228,"lng":121.056089},{"lat":14.54712,"lng":121.055986},
{"lat":14.547002,"lng":121.055837},{"lat":14.546919,"lng":121.055739},{"lat":14.546863,"lng":121.055638},{"lat":14.546845,"lng":121.05558},{"lat":14.546852,"lng":121.055513},
{"lat":14.546891,"lng":121.055443},{"lat":14.546944,"lng":121.055396},{"lat":14.547041,"lng":121.055306},{"lat":14.547075,"lng":121.055269},{"lat":14.547141,"lng":121.055188},
{"lat":14.547196,"lng":121.05511},{"lat":14.547247,"lng":121.055029},{"lat":14.547263,"lng":121.054997},{"lat":14.547293,"lng":121.054939},{"lat":14.547346,"lng":121.054832},
{"lat":14.547368,"lng":121.054771},{"lat":14.54742,"lng":121.054623},{"lat":14.547465,"lng":121.054405},{"lat":14.547471,"lng":121.05436},{"lat":14.547475,"lng":121.054314},
{"lat":14.547486,"lng":121.054207},{"lat":14.547479,"lng":121.054107},{"lat":14.547458,"lng":121.053972},{"lat":14.547439,"lng":121.053853},{"lat":14.547337,"lng":121.053236},
{"lat":14.54732,"lng":121.052871},{"lat":14.54734,"lng":121.052528},{"lat":14.547348,"lng":121.052452},{"lat":14.547361,"lng":121.052384},{"lat":14.547381,"lng":121.052315},
{"lat":14.547463,"lng":121.052343},{"lat":14.547816,"lng":121.052462},{"lat":14.548222,"lng":121.052598},{"lat":14.548308,"lng":121.052627},{"lat":14.548745,"lng":121.052774},
{"lat":14.548836,"lng":121.052804},{"lat":14.548961,"lng":121.052846},{"lat":14.549546,"lng":121.053043},{"lat":14.549653,"lng":121.053079},{"lat":14.549926,"lng":121.053171},
{"lat":14.550205,"lng":121.053265},{"lat":14.550553,"lng":121.053382},{"lat":14.550883,"lng":121.053494},{"lat":14.550977,"lng":121.053524},{"lat":14.551005,"lng":121.053435},
{"lat":14.551033,"lng":121.053348},{"lat":14.5512,"lng":121.052829},{"lat":14.551271,"lng":121.052611},{"lat":14.551306,"lng":121.052502},{"lat":14.551377,"lng":121.052282},
{"lat":14.551419,"lng":121.052152},{"lat":14.551539,"lng":121.051781},{"lat":14.551567,"lng":121.051694},{"lat":14.551596,"lng":121.051604},{"lat":14.551715,"lng":121.051232},
{"lat":14.551832,"lng":121.050866},{"lat":14.551867,"lng":121.050756},{"lat":14.551979,"lng":121.050405},{"lat":14.552103,"lng":121.050017},{"lat":14.552133,"lng":121.049926},
{"lat":14.552045,"lng":121.049896},{"lat":14.551627,"lng":121.049754},{"lat":14.551581,"lng":121.04974},{"lat":14.551531,"lng":121.049723},{"lat":14.551243,"lng":121.049626},
{"lat":14.55122,"lng":121.049618},{"lat":14.551161,"lng":121.0496},{"lat":14.551035,"lng":121.049556},{"lat":14.550923,"lng":121.049517},{"lat":14.550602,"lng":121.049409},
{"lat":14.550545,"lng":121.049391},{"lat":14.550495,"lng":121.049374},{"lat":14.550073,"lng":121.049234},{"lat":14.549986,"lng":121.049204},{"lat":14.549903,"lng":121.049175},
{"lat":14.549502,"lng":121.049038},{"lat":14.549432,"lng":121.049015},{"lat":14.549361,"lng":121.048991},{"lat":14.549189,"lng":121.048931},{"lat":14.548833,"lng":121.048811},
{"lat":14.548616,"lng":121.048738},{"lat":14.54859,"lng":121.048729},{"lat":14.548514,"lng":121.048705},{"lat":14.548534,"lng":121.048644},{"lat":14.548541,"lng":121.048621},
{"lat":14.548544,"lng":121.048609},{"lat":14.54866,"lng":121.048235},{"lat":14.548905,"lng":121.047438},{"lat":14.548967,"lng":121.047242},{"lat":14.549033,"lng":121.04702},
{"lat":14.54905,"lng":121.046961},{"lat":14.549021,"lng":121.046847},{"lat":14.549048,"lng":121.04677},{"lat":14.549076,"lng":121.046678},{"lat":14.549205,"lng":121.04626},
{"lat":14.549253,"lng":121.046104},{"lat":14.549276,"lng":121.046034},{"lat":14.549307,"lng":121.045935},{"lat":14.549403,"lng":121.045633},{"lat":14.549428,"lng":121.045557},
{"lat":14.549439,"lng":121.045524},{"lat":14.549523,"lng":121.045221},{"lat":14.549554,"lng":121.045137},{"lat":14.549672,"lng":121.045179},{"lat":14.550025,"lng":121.045304},
{"lat":14.550299,"lng":121.045397},{"lat":14.55045,"lng":121.045454},{"lat":14.550881,"lng":121.045598},{"lat":14.550912,"lng":121.045608},{"lat":14.551243,"lng":121.045713},
{"lat":14.551301,"lng":121.045732},{"lat":14.551334,"lng":121.045742},{"lat":14.551393,"lng":121.045762},{"lat":14.552138,"lng":121.04602},{"lat":14.552163,"lng":121.046028},
{"lat":14.552525,"lng":121.046162},{"lat":14.552931,"lng":121.046293},{"lat":14.552978,"lng":121.04631},{"lat":14.553043,"lng":121.046332},{"lat":14.553121,"lng":121.046359},
{"lat":14.553647,"lng":121.046541},{"lat":14.55386,"lng":121.046614},{"lat":14.5539,"lng":121.046628},{"lat":14.553923,"lng":121.046636},{"lat":14.554141,"lng":121.046711},
{"lat":14.554212,"lng":121.046704},{"lat":14.554278,"lng":121.046687},{"lat":14.55432,"lng":121.046668},{"lat":14.554364,"lng":121.046635},{"lat":14.55439,"lng":121.046586},
{"lat":14.554399,"lng":121.046531},{"lat":14.554397,"lng":121.046472},{"lat":14.554358,"lng":121.046374},{"lat":14.554298,"lng":121.046314},{"lat":14.554135,"lng":121.04617},
{"lat":14.554034,"lng":121.046086},{"lat":14.55406,"lng":121.046058},{"lat":14.554095,"lng":121.046006},{"lat":14.554421,"lng":121.044985},{"lat":14.554447,"lng":121.044903},
{"lat":14.554474,"lng":121.044803},{"lat":14.554516,"lng":121.044667},{"lat":14.554695,"lng":121.044098},{"lat":14.554721,"lng":121.044026},{"lat":14.554664,"lng":121.044002},
{"lat":14.553942,"lng":121.043761},{"lat":14.553876,"lng":121.043736},{"lat":14.553852,"lng":121.043811},{"lat":14.553813,"lng":121.043934},{"lat":14.553744,"lng":121.04415},
{"lat":14.553717,"lng":121.04423},{"lat":14.553668,"lng":121.044385},{"lat":14.553614,"lng":121.044562},{"lat":14.55361,"lng":121.044573},{"lat":14.553606,"lng":121.044586},
{"lat":14.553595,"lng":121.044621},{"lat":14.553582,"lng":121.044662},{"lat":14.553575,"lng":121.044684},{"lat":14.553468,"lng":121.04502},{"lat":14.55344,"lng":121.045109},
{"lat":14.553404,"lng":121.04522},{"lat":14.553316,"lng":121.0455},{"lat":14.553288,"lng":121.045587},{"lat":14.553214,"lng":121.045548},{"lat":14.553081,"lng":121.045481},
{"lat":14.552894,"lng":121.045389},{"lat":14.552816,"lng":121.04536},{"lat":14.552752,"lng":121.045337},{"lat":14.552533,"lng":121.045255},{"lat":14.552528,"lng":121.045253},
{"lat":14.552428,"lng":121.045216},{"lat":14.55237,"lng":121.045195},{"lat":14.552366,"lng":121.045193},{"lat":14.552226,"lng":121.045143},{"lat":14.552116,"lng":121.045109},
{"lat":14.552025,"lng":121.045083},{"lat":14.551811,"lng":121.045026},{"lat":14.551704,"lng":121.045003},{"lat":14.551638,"lng":121.044994},{"lat":14.551568,"lng":121.044984},
{"lat":14.551538,"lng":121.045094},{"lat":14.551465,"lng":121.045333},{"lat":14.551357,"lng":121.045671},{"lat":14.551334,"lng":121.045742},{"lat":14.551308,"lng":121.045818},
{"lat":14.551213,"lng":121.04611},{"lat":14.551189,"lng":121.046181},{"lat":14.551183,"lng":121.0462},{"lat":14.551169,"lng":121.046244},{"lat":14.551134,"lng":121.046348},
{"lat":14.551071,"lng":121.046542},{"lat":14.551039,"lng":121.046639},{"lat":14.551021,"lng":121.0467},{"lat":14.550955,"lng":121.046908},{"lat":14.550942,"lng":121.046949},
{"lat":14.550903,"lng":121.047074},{"lat":14.550879,"lng":121.047148},{"lat":14.55081,"lng":121.047367},{"lat":14.550778,"lng":121.047469},{"lat":14.550753,"lng":121.047547},
{"lat":14.550851,"lng":121.047581},{"lat":14.551344,"lng":121.047753},{"lat":14.551469,"lng":121.047797},{"lat":14.551593,"lng":121.04784},{"lat":14.55174,"lng":121.047889},
{"lat":14.55184,"lng":121.047922},{"lat":14.552384,"lng":121.048103},{"lat":14.552465,"lng":121.04813},{"lat":14.552571,"lng":121.048164},{"lat":14.553066,"lng":121.048331},
{"lat":14.553519,"lng":121.048484},{"lat":14.553592,"lng":121.048508},{"lat":14.554049,"lng":121.04865},{"lat":14.554079,"lng":121.048658},{"lat":14.554162,"lng":121.04867},
{"lat":14.554141,"lng":121.048738},{"lat":14.554064,"lng":121.048984},{"lat":14.553973,"lng":121.049269},{"lat":14.55382,"lng":121.049741},{"lat":14.553629,"lng":121.050336},
{"lat":14.5536,"lng":121.050427},{"lat":14.55353,"lng":121.050402},{"lat":14.553105,"lng":121.050254},{"lat":14.552869,"lng":121.050174},{"lat":14.552683,"lng":121.050111},
{"lat":14.552218,"lng":121.049955},{"lat":14.552133,"lng":121.049926},{"lat":14.552045,"lng":121.049896},{"lat":14.551627,"lng":121.049754},{"lat":14.551581,"lng":121.04974},
{"lat":14.551531,"lng":121.049723},{"lat":14.551243,"lng":121.049626},{"lat":14.55122,"lng":121.049618},{"lat":14.551161,"lng":121.0496},{"lat":14.551035,"lng":121.049556},
{"lat":14.550923,"lng":121.049517},{"lat":14.550602,"lng":121.049409},{"lat":14.550545,"lng":121.049391},{"lat":14.550495,"lng":121.049374},{"lat":14.550073,"lng":121.049234},
{"lat":14.549986,"lng":121.049204},{"lat":14.549956,"lng":121.049293},{"lat":14.549863,"lng":121.049597},{"lat":14.549727,"lng":121.050021},{"lat":14.549694,"lng":121.050123},
{"lat":14.549452,"lng":121.050885},{"lat":14.549423,"lng":121.050973},{"lat":14.549392,"lng":121.051069},{"lat":14.549251,"lng":121.051488},{"lat":14.549149,"lng":121.051814},
{"lat":14.549022,"lng":121.05222},{"lat":14.548897,"lng":121.052611},{"lat":14.548868,"lng":121.052703},{"lat":14.548836,"lng":121.052804},{"lat":14.548961,"lng":121.052846},
{"lat":14.549546,"lng":121.053043},{"lat":14.549653,"lng":121.053079},{"lat":14.549926,"lng":121.053171},{"lat":14.550205,"lng":121.053265},{"lat":14.550553,"lng":121.053382},
{"lat":14.550883,"lng":121.053494},{"lat":14.550977,"lng":121.053524},{"lat":14.551058,"lng":121.053552},{"lat":14.551944,"lng":121.053852},{"lat":14.552042,"lng":121.053882},
{"lat":14.552393,"lng":121.053998},{"lat":14.552449,"lng":121.054017},{"lat":14.552432,"lng":121.054068},{"lat":14.552282,"lng":121.054567},{"lat":14.55214,"lng":121.055045},
{"lat":14.551972,"lng":121.055607},{"lat":14.551951,"lng":121.055676},{"lat":14.55186,"lng":121.055978},{"lat":14.551822,"lng":121.056112},{"lat":14.551746,"lng":121.056359},
{"lat":14.551731,"lng":121.056409},{"lat":14.551714,"lng":121.056464},{"lat":14.551698,"lng":121.056516},{"lat":14.551579,"lng":121.056898},{"lat":14.551462,"lng":121.057269},
{"lat":14.551451,"lng":121.057299}
]$c_line$;

  v_c_stops text := $c_stops$[
{"name":"Market! Market!","lat":14.548854,"lng":121.056425,"terminal":true},
{"name":"NutriAsia","lat":14.551715,"lng":121.051232},
{"name":"The Fort Station","lat":14.549017,"lng":121.04732},
{"name":"Net One","lat":14.550223,"lng":121.045424},
{"name":"Bonifacio Stopover","lat":14.554238,"lng":121.045817},
{"name":"Crescent Park West","lat":14.554441,"lng":121.043841},
{"name":"The Globe Tower","lat":14.552887,"lng":121.050107},
{"name":"One Parkade","lat":14.549834,"lng":121.049474},
{"name":"University Parkway","lat":14.551504,"lng":121.056888}
]$c_stops$;
begin
  if exists (select 1 from public.routes_before_osm) then
    raise exception 'The routes were already replaced. Run 2026-09-26-real-bgc-routes-rollback.sql first to go back.';
  end if;

  if (select count(*) from public.routes where route_name ilike '%north%') <> 1 then
    raise exception 'Expected exactly one route named like North. The routes are: %.',
      (select string_agg(route_id || ' ' || route_name, ', ' order by route_id) from public.routes);
  end if;
  select route_id into v_north from public.routes where route_name ilike '%north%';

  if (select count(*) from public.routes where route_id <> v_north) <> 1 then
    raise exception 'Expected exactly one route besides North Express. The routes are: %.',
      (select string_agg(route_id || ' ' || route_name, ', ' order by route_id) from public.routes);
  end if;
  select route_id into v_second from public.routes where route_id <> v_north;

  insert into public.routes_before_osm
    (route_id, route_name, origin, destination, waypoints_json, stops_json, updated_at)
  select route_id, route_name, origin, destination, waypoints_json, stops_json, updated_at
    from public.routes
   where route_id in (v_north, v_second);

  update public.routes
     set origin         = 'EDSA-Ayala Terminal',
         destination    = 'The Fort Strip',
         waypoints_json = v_nx_line::jsonb::text,
         stops_json     = v_nx_stops::jsonb::text,
         updated_at     = now()
   where route_id = v_north;

  update public.routes
     set route_name     = case when route_name ilike '%south line%'
                               then regexp_replace(route_name, 'south line', 'Central', 'i')
                               else 'Route 02 – Central' end,
         origin         = 'Market! Market!',
         destination    = 'University Parkway',
         waypoints_json = v_c_line::jsonb::text,
         stops_json     = v_c_stops::jsonb::text,
         updated_at     = now()
   where route_id = v_second;
end
$routes$;

alter table public.telemetry_data
  add column if not exists accuracy real;

alter table public.telemetry_data
  drop constraint if exists ck_telemetry_accuracy;
alter table public.telemetry_data
  add constraint ck_telemetry_accuracy check (accuracy is null or accuracy >= 0);

comment on column public.telemetry_data.accuracy is
  'Radius in metres within which the phone places the fix, as Android reports it. Null from builds that do not send it.';

commit;

-- What the routes now hold.
select route_id, route_name, origin, destination,
       jsonb_array_length(waypoints_json::jsonb) as line_points,
       jsonb_array_length(stops_json::jsonb)     as stops,
       (select string_agg(s->>'name', ' > ' order by i)
          from jsonb_array_elements(stops_json::jsonb) with ordinality as e(s, i)) as stop_order,
       (select s->>'name' from jsonb_array_elements(stops_json::jsonb) s
         where (s->>'terminal')::boolean) as terminal
  from public.routes
 order by route_id;
