# OurAirports runway snapshot

`airports.csv` and `runways.csv` are the worldwide OurAirports open-data snapshot
downloaded on 2026-07-18 for Challenge Lab's offline runway lookup.

- Source: https://ourairports.com/data/
- Schema: https://ourairports.com/help/data-dictionary.html
- Terms: public domain, with no guarantee of accuracy or fitness for use

The application treats each `le_*` / `he_*` coordinate as the physical runway end and
projects forward by that end's `*_displaced_threshold_ft` value to obtain the usable
landing threshold. Replace both files together when refreshing the snapshot.
