# Column-Level Lineage (generated)

> **This document is generated from code.** The graph below is produced by
> `LineageCatalog.Build().ToMermaid()` (`src/Lakehouse.Application/Lineage/LineageCatalog.cs`) and
> served live at `GET /api/lineage/mermaid`. Every edge corresponds to a real transformation
> performed by `BronzeIngestor`, `SilverBuilder` or `GoldBuilder` - it is **not** hand-drawn.

## Why this exists

Column-level lineage answers two questions a hand-drawn diagram never can, because it drifts from
the code the moment either changes:

1. **Provenance / upstream** - `GET /api/lineage/upstream?column=agg_daily_revenue.revenue_usd`
2. **Impact analysis** - `GET /api/lineage/impact?column=silver_order_lines.net_amount`

Both are transitive graph walks over the edges below (`LineageGraph.Upstream` / `LineageGraph.Impact`).

### Worked example - multi-hop provenance of revenue_usd

```
agg_daily_revenue.revenue_usd
  <- fact_order_line.net_amount_usd         sum(net_amount_usd)
       <- silver_order_lines.net_amount     net_amount * fx.rate_to_usd (as-of order_date)
       <- silver_fx_rates.rate_to_usd
            <- bronze_order_lines.*          typed parse, dedup by key+sequence
                 <- source_order_lines.*     append-only ingest, source-stamped
```

The test `LineageTests.Upstream_of_a_gold_column_reaches_silver_bronze_and_source` asserts this
chain crosses all four layers; `Impact_of_a_source_column_reaches_the_gold_marts` asserts the reverse.

## Full lineage graph

```mermaid
flowchart LR
    subgraph g_agg_cohort_retention["agg_cohort_retention"]
        n960018238["activity_month"]
        n176789962["cohort_month"]
        n483606950["customers"]
    end
    subgraph g_agg_daily_revenue["agg_daily_revenue"]
        n1950883457["date_key"]
        n2082235243["lines"]
        n1662573866["orders"]
        n1311457297["revenue_usd"]
        n77952413["units"]
    end
    subgraph g_agg_funnel["agg_funnel"]
        n1262053512["sessions"]
        n1116212423["step"]
        n479643854["step_order"]
    end
    subgraph g_agg_inventory_position["agg_inventory_position"]
        n2118231851["as_of_date"]
        n1406352083["position"]
        n324348146["product_id"]
    end
    subgraph g_bronze_clickstream["bronze_clickstream"]
        n1589656390["channel"]
        n50411075["customer_id"]
        n434738018["event_id"]
        n663541827["event_ts"]
        n1966241745["event_type"]
        n265013466["page"]
        n2050010032["session_id"]
    end
    subgraph g_bronze_customers["bronze_customers"]
        n1117952868["_commit_ts"]
        n699065231["city"]
        n358982801["country"]
        n1848954676["created_at"]
        n84819087["currency"]
        n1453047689["customer_id"]
        n741328594["email"]
        n562899855["loyalty_tier"]
        n58045337["name"]
        n1450412533["segment"]
        n373499590["updated_at"]
    end
    subgraph g_bronze_fx_rates["bronze_fx_rates"]
        n511770826["currency"]
        n525270599["rate_date"]
        n965770518["rate_to_usd"]
    end
    subgraph g_bronze_inventory_movements["bronze_inventory_movements"]
        n874159615["delta_qty"]
        n789797275["event_ts"]
        n1132088984["movement_id"]
        n2084747128["product_id"]
        n1620898752["reason"]
    end
    subgraph g_bronze_order_lines["bronze_order_lines"]
        n1925669863["currency"]
        n182070841["discount"]
        n191977612["order_id"]
        n277320291["order_line_id"]
        n668613100["product_id"]
        n606454496["quantity"]
        n8632999["unit_price"]
    end
    subgraph g_bronze_orders["bronze_orders"]
        n142297089["channel"]
        n692221858["currency"]
        n420274271["customer_id"]
        n1729553319["order_id"]
        n1641980424["order_ts"]
        n1956282876["status"]
        n831897246["updated_at"]
    end
    subgraph g_bronze_products["bronze_products"]
        n1387877576["_commit_ts"]
        n1865956691["active"]
        n1884006515["category"]
        n1862136829["created_at"]
        n1742188415["currency"]
        n1998520247["name"]
        n1799994418["product_id"]
        n241337331["unit_price"]
        n1156662097["updated_at"]
    end
    subgraph g_dim_customer["dim_customer"]
        n422987673["city"]
        n452335633["country"]
        n2064220086["currency"]
        n121304726["customer_id"]
        n439224930["customer_sk"]
        n1364474565["email"]
        n1030977830["is_current"]
        n719927368["loyalty_tier"]
        n1230585244["name"]
        n638819962["segment"]
        n870747360["valid_from"]
        n1352408394["valid_to"]
    end
    subgraph g_dim_product["dim_product"]
        n158111921["active"]
        n253729809["category"]
        n2004310424["currency"]
        n1658185867["is_current"]
        n923500780["name"]
        n1404699618["product_id"]
        n890616159["product_sk"]
        n1306571560["unit_price"]
        n113421114["valid_from"]
        n201187106["valid_to"]
    end
    subgraph g_fact_clickstream_session["fact_clickstream_session"]
        n611026938["add_to_carts"]
        n837515162["channel"]
        n747752407["checkouts"]
        n1379792445["converted"]
        n910362605["customer_sk"]
        n1969740592["end_ts"]
        n947121940["event_count"]
        n413529157["page_views"]
        n521195338["purchases"]
        n2043674656["session_date_key"]
        n663937167["session_id"]
        n1879939333["start_ts"]
    end
    subgraph g_fact_order_line["fact_order_line"]
        n1717488604["channel"]
        n271269136["currency"]
        n2072066294["customer_sk"]
        n1488185785["discount"]
        n95381082["gross_amount"]
        n20140314["net_amount"]
        n705611615["net_amount_usd"]
        n1223332943["order_date_key"]
        n217195071["order_id"]
        n1786128958["order_line_id"]
        n565059510["product_sk"]
        n1214889714["quantity"]
        n268520954["unit_price"]
    end
    subgraph g_silver_clickstream["silver_clickstream"]
        n417445877["channel"]
        n1755399951["customer_id"]
        n1871188241["event_date"]
        n1190300587["event_id"]
        n1482383326["event_ts"]
        n1791543854["event_type"]
        n1377780136["page"]
        n2022006004["session_id"]
    end
    subgraph g_silver_customers["silver_customers"]
        n491486296["city"]
        n2100899880["country"]
        n1867852357["currency"]
        n184643154["customer_id"]
        n125267343["email"]
        n1666886310["is_current"]
        n2105418203["loyalty_tier"]
        n1597692858["name"]
        n128705602["segment"]
        n1059452648["surrogate_key"]
        n819207569["valid_from"]
        n131174527["valid_to"]
    end
    subgraph g_silver_fx_rates["silver_fx_rates"]
        n1227056384["currency"]
        n1447224090["rate_date"]
        n1351527962["rate_to_usd"]
    end
    subgraph g_silver_order_lines["silver_order_lines"]
        n1468412587["currency"]
        n209600715["discount"]
        n1729900105["gross_amount"]
        n1021606779["net_amount"]
        n71836519["order_id"]
        n1165890459["order_line_id"]
        n1182332943["product_id"]
        n1822416490["quantity"]
        n1685240239["unit_price"]
    end
    subgraph g_silver_orders["silver_orders"]
        n1724128740["channel"]
        n1841961066["currency"]
        n1388065084["customer_id"]
        n819514034["order_date"]
        n1419359712["order_id"]
        n2062971675["order_ts"]
        n826089756["status"]
    end
    subgraph g_silver_products["silver_products"]
        n1286260990["active"]
        n2035500035["category"]
        n962785351["currency"]
        n1954196640["is_current"]
        n2042600374["name"]
        n320934269["product_id"]
        n870194547["surrogate_key"]
        n805415642["unit_price"]
        n467016690["valid_from"]
        n1941023022["valid_to"]
    end
    subgraph g_source_clickstream["source_clickstream"]
        n743431300["channel"]
        n866867090["customer_id"]
        n1983493655["event_id"]
        n810958513["event_ts"]
        n1354458625["event_type"]
        n720148398["page"]
        n77224914["session_id"]
    end
    subgraph g_source_customers["source_customers"]
        n1880691044["city"]
        n1054066351["country"]
        n1428138982["created_at"]
        n809856877["currency"]
        n345604323["customer_id"]
        n1475871003["email"]
        n1799855687["loyalty_tier"]
        n1883044135["name"]
        n1581382527["segment"]
        n1574245180["updated_at"]
    end
    subgraph g_source_fx_rates["source_fx_rates"]
        n664083406["currency"]
        n412465480["rate_date"]
        n1181707888["rate_to_usd"]
    end
    subgraph g_source_inventory_movements["source_inventory_movements"]
        n983417537["delta_qty"]
        n234778321["event_ts"]
        n1888353429["movement_id"]
        n1097607182["product_id"]
        n1396339520["reason"]
    end
    subgraph g_source_order_lines["source_order_lines"]
        n1838780128["currency"]
        n1994787222["discount"]
        n1658479961["order_id"]
        n383345512["order_line_id"]
        n1183986179["product_id"]
        n1389047087["quantity"]
        n1273157975["unit_price"]
    end
    subgraph g_source_orders["source_orders"]
        n2107651929["channel"]
        n1431480831["currency"]
        n1170956616["customer_id"]
        n721967031["order_id"]
        n1541252091["order_ts"]
        n1660212612["status"]
        n491635240["updated_at"]
    end
    subgraph g_source_products["source_products"]
        n76941775["active"]
        n1382498876["category"]
        n1775006754["created_at"]
        n871040827["currency"]
        n1457676996["name"]
        n2126532768["product_id"]
        n991210157["unit_price"]
        n486749764["updated_at"]
    end
    n345604323 -->|ingest (append-only, source-stamped)| n1453047689
    n1883044135 -->|ingest (append-only, source-stamped)| n58045337
    n1475871003 -->|ingest (append-only, source-stamped)| n741328594
    n1880691044 -->|ingest (append-only, source-stamped)| n699065231
    n1054066351 -->|ingest (append-only, source-stamped)| n358982801
    n809856877 -->|ingest (append-only, source-stamped)| n84819087
    n1581382527 -->|ingest (append-only, source-stamped)| n1450412533
    n1428138982 -->|ingest (append-only, source-stamped)| n1848954676
    n1574245180 -->|ingest (append-only, source-stamped)| n373499590
    n1799855687 -->|ingest (append-only, source-stamped)| n562899855
    n2126532768 -->|ingest (append-only, source-stamped)| n1799994418
    n1457676996 -->|ingest (append-only, source-stamped)| n1998520247
    n1382498876 -->|ingest (append-only, source-stamped)| n1884006515
    n991210157 -->|ingest (append-only, source-stamped)| n241337331
    n871040827 -->|ingest (append-only, source-stamped)| n1742188415
    n76941775 -->|ingest (append-only, source-stamped)| n1865956691
    n1775006754 -->|ingest (append-only, source-stamped)| n1862136829
    n486749764 -->|ingest (append-only, source-stamped)| n1156662097
    n721967031 -->|ingest (append-only, source-stamped)| n1729553319
    n1170956616 -->|ingest (append-only, source-stamped)| n420274271
    n1541252091 -->|ingest (append-only, source-stamped)| n1641980424
    n2107651929 -->|ingest (append-only, source-stamped)| n142297089
    n1431480831 -->|ingest (append-only, source-stamped)| n692221858
    n1660212612 -->|ingest (append-only, source-stamped)| n1956282876
    n491635240 -->|ingest (append-only, source-stamped)| n831897246
    n383345512 -->|ingest (append-only, source-stamped)| n277320291
    n1658479961 -->|ingest (append-only, source-stamped)| n191977612
    n1183986179 -->|ingest (append-only, source-stamped)| n668613100
    n1389047087 -->|ingest (append-only, source-stamped)| n606454496
    n1273157975 -->|ingest (append-only, source-stamped)| n8632999
    n1994787222 -->|ingest (append-only, source-stamped)| n182070841
    n1838780128 -->|ingest (append-only, source-stamped)| n1925669863
    n1983493655 -->|ingest (append-only, source-stamped)| n434738018
    n77224914 -->|ingest (append-only, source-stamped)| n2050010032
    n866867090 -->|ingest (append-only, source-stamped)| n50411075
    n810958513 -->|ingest (append-only, source-stamped)| n663541827
    n720148398 -->|ingest (append-only, source-stamped)| n265013466
    n1354458625 -->|ingest (append-only, source-stamped)| n1966241745
    n743431300 -->|ingest (append-only, source-stamped)| n1589656390
    n1888353429 -->|ingest (append-only, source-stamped)| n1132088984
    n1097607182 -->|ingest (append-only, source-stamped)| n2084747128
    n234778321 -->|ingest (append-only, source-stamped)| n789797275
    n983417537 -->|ingest (append-only, source-stamped)| n874159615
    n1396339520 -->|ingest (append-only, source-stamped)| n1620898752
    n664083406 -->|ingest (append-only, source-stamped)| n511770826
    n412465480 -->|ingest (append-only, source-stamped)| n525270599
    n1181707888 -->|ingest (append-only, source-stamped)| n965770518
    n1453047689 -->|hash(customer_id | valid_from)| n1059452648
    n1117952868 -->|hash(customer_id | valid_from)| n1059452648
    n1453047689 -->|typed passthrough| n184643154
    n58045337 -->|SCD2 tracked attribute| n1597692858
    n741328594 -->|SCD2 tracked attribute| n125267343
    n699065231 -->|SCD2 tracked attribute| n491486296
    n358982801 -->|SCD2 tracked attribute| n2100899880
    n84819087 -->|SCD2 tracked attribute| n1867852357
    n1450412533 -->|SCD2 tracked attribute| n128705602
    n562899855 -->|SCD2 tracked attribute| n2105418203
    n1117952868 -->|SCD2 valid_from = commit_ts| n819207569
    n1117952868 -->|SCD2 valid_to = next version commit_ts| n131174527
    n1117952868 -->|SCD2 open-segment flag| n1666886310
    n1799994418 -->|hash(product_id | valid_from)| n870194547
    n1387877576 -->|hash(product_id | valid_from)| n870194547
    n1799994418 -->|typed passthrough| n320934269
    n1998520247 -->|SCD2 tracked attribute (typed)| n2042600374
    n1884006515 -->|SCD2 tracked attribute (typed)| n2035500035
    n241337331 -->|SCD2 tracked attribute (typed)| n805415642
    n1742188415 -->|SCD2 tracked attribute (typed)| n962785351
    n1865956691 -->|SCD2 tracked attribute (typed)| n1286260990
    n1387877576 -->|SCD2 valid_from = commit_ts| n467016690
    n1387877576 -->|SCD2 valid_to = next version commit_ts| n1941023022
    n1387877576 -->|SCD2 open-segment flag| n1954196640
    n1729553319 -->|dedup by business key + sequence| n1419359712
    n420274271 -->|referential-integrity conformance| n1388065084
    n1641980424 -->|parse timestamp → UTC (tz normalise)| n2062971675
    n1641980424 -->|date(order_ts)| n819514034
    n142297089 -->|typed passthrough| n1724128740
    n692221858 -->|typed passthrough| n1841961066
    n1956282876 -->|typed passthrough| n826089756
    n277320291 -->|dedup by business key + sequence| n1165890459
    n191977612 -->|referential-integrity conformance| n71836519
    n668613100 -->|referential-integrity conformance| n1182332943
    n606454496 -->|parse long (> 0)| n1822416490
    n8632999 -->|parse decimal (>= 0)| n1685240239
    n182070841 -->|parse decimal (>= 0)| n209600715
    n1925669863 -->|typed passthrough| n1468412587
    n606454496 -->|quantity * unit_price| n1729900105
    n8632999 -->|quantity * unit_price| n1729900105
    n606454496 -->|quantity * unit_price - discount| n1021606779
    n8632999 -->|quantity * unit_price - discount| n1021606779
    n182070841 -->|quantity * unit_price - discount| n1021606779
    n434738018 -->|typed passthrough / dedup| n1190300587
    n2050010032 -->|typed passthrough / dedup| n2022006004
    n50411075 -->|typed passthrough / dedup| n1755399951
    n265013466 -->|typed passthrough / dedup| n1377780136
    n1966241745 -->|typed passthrough / dedup| n1791543854
    n1589656390 -->|typed passthrough / dedup| n417445877
    n663541827 -->|parse timestamp → UTC| n1482383326
    n663541827 -->|date(event_ts)| n1871188241
    n511770826 -->|typed passthrough| n1227056384
    n525270599 -->|typed passthrough| n1447224090
    n965770518 -->|parse decimal (> 0)| n1351527962
    n1059452648 -->|conform surrogate key| n439224930
    n184643154 -->|conform dimension| n121304726
    n1597692858 -->|conform dimension| n1230585244
    n125267343 -->|conform dimension| n1364474565
    n491486296 -->|conform dimension| n422987673
    n2100899880 -->|conform dimension| n452335633
    n1867852357 -->|conform dimension| n2064220086
    n128705602 -->|conform dimension| n638819962
    n2105418203 -->|conform dimension| n719927368
    n819207569 -->|conform dimension| n870747360
    n131174527 -->|conform dimension| n1352408394
    n1666886310 -->|conform dimension| n1030977830
    n870194547 -->|conform surrogate key| n890616159
    n320934269 -->|conform dimension| n1404699618
    n2042600374 -->|conform dimension| n923500780
    n2035500035 -->|conform dimension| n253729809
    n805415642 -->|conform dimension| n1306571560
    n962785351 -->|conform dimension| n2004310424
    n1286260990 -->|conform dimension| n158111921
    n467016690 -->|conform dimension| n113421114
    n1941023022 -->|conform dimension| n201187106
    n1954196640 -->|conform dimension| n1658185867
    n1165890459 -->|passthrough| n1786128958
    n71836519 -->|passthrough| n217195071
    n2062971675 -->|yyyymmdd(order_ts)| n1223332943
    n439224930 -->|SCD2 effective-version join at order_ts| n2072066294
    n2062971675 -->|SCD2 effective-version join at order_ts| n2072066294
    n1388065084 -->|SCD2 effective-version join at order_ts| n2072066294
    n890616159 -->|SCD2 effective-version join at order_ts| n565059510
    n2062971675 -->|SCD2 effective-version join at order_ts| n565059510
    n1182332943 -->|SCD2 effective-version join at order_ts| n565059510
    n1724128740 -->|passthrough| n1717488604
    n1468412587 -->|passthrough| n271269136
    n1822416490 -->|passthrough| n1214889714
    n1685240239 -->|passthrough| n268520954
    n209600715 -->|passthrough| n1488185785
    n1729900105 -->|passthrough| n95381082
    n1021606779 -->|passthrough| n20140314
    n1021606779 -->|net_amount * fx.rate_to_usd (as-of order_date)| n705611615
    n1351527962 -->|net_amount * fx.rate_to_usd (as-of order_date)| n705611615
    n2022006004 -->|group by session| n663937167
    n439224930 -->|SCD2 effective-version join at session start| n910362605
    n1755399951 -->|SCD2 effective-version join at session start| n910362605
    n1482383326 -->|SCD2 effective-version join at session start| n910362605
    n1482383326 -->|yyyymmdd(min(event_ts))| n2043674656
    n417445877 -->|first channel in session| n837515162
    n1482383326 -->|min(event_ts)| n1879939333
    n1482383326 -->|max(event_ts)| n1969740592
    n1791543854 -->|count by event_type| n947121940
    n1791543854 -->|count by event_type| n413529157
    n1791543854 -->|count by event_type| n611026938
    n1791543854 -->|count by event_type| n747752407
    n1791543854 -->|count by event_type| n521195338
    n1791543854 -->|count by event_type| n1379792445
    n1223332943 -->|group by order_date_key| n1950883457
    n217195071 -->|count distinct order_id| n1662573866
    n1786128958 -->|count| n2082235243
    n1214889714 -->|sum(quantity)| n77952413
    n705611615 -->|sum(net_amount_usd)| n1311457297
    n1388065084 -->|min order month per customer| n176789962
    n2062971675 -->|min order month per customer| n176789962
    n2062971675 -->|order month| n960018238
    n1388065084 -->|count distinct customers| n483606950
    n521195338 -->|funnel step counts| n1262053512
    n413529157 -->|funnel step counts| n1262053512
    n521195338 -->|funnel step counts| n1116212423
    n413529157 -->|funnel step counts| n1116212423
    n521195338 -->|funnel step counts| n479643854
    n413529157 -->|funnel step counts| n479643854
    n874159615 -->|sum(delta_qty)| n1406352083
    n2084747128 -->|group by product| n324348146
    n789797275 -->|max(event_ts)| n2118231851
```

