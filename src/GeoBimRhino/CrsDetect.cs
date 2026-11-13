// geoBIM.app for Rhino
// © 2026 Christof Lorenz
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Linq;

namespace GeoBimRhino
{
    public class CrsCandidate
    {
        public int Epsg;
        public string Option;   // Rhino command-line option name (letters and digits only)
        public string Label;
        public CrsCandidate(int epsg, string option, string label) { Epsg = epsg; Option = option; Label = label; }
    }

    public class CrsGuess
    {
        /// <summary>Unambiguous code, else null (then ask among Candidates)</summary>
        public int? Epsg;
        public List<CrsCandidate> Candidates = new List<CrsCandidate>();
        public string Region = "";
    }

    /// <summary>
    /// Which projected CRS do map coordinates (metres, model centre) belong to?
    /// Unambiguous: UTM with zone prefix (31/32/33 → 5649/4647/5650), Swiss
    /// LV95 (2056), German Gauss-Krüger with zone prefix (31466–31469), UTM
    /// without prefix (the default UTM code). Ambiguous — the coordinate ranges
    /// overlap — are Swiss LV03 and the Austrian systems (MGI Gauss-Krüger
    /// West/Central/East, BMN M28/M31/M34, Lambert MGI / ETRS89): the plugin
    /// then asks once per document, offering only the plausible candidates.
    /// No Rhino types: testable on its own.
    /// </summary>
    public static class CrsDetect
    {
        public static readonly CrsCandidate LV95 = new CrsCandidate(2056, "SwissLV95", "CH1903+ / LV95 (Switzerland)");
        public static readonly CrsCandidate LV03 = new CrsCandidate(21781, "SwissLV03", "CH1903 / LV03 (Switzerland, old)");
        public static readonly CrsCandidate AtGkWest = new CrsCandidate(31254, "AustriaGKWest", "MGI / Austria GK West");
        public static readonly CrsCandidate AtGkCentral = new CrsCandidate(31255, "AustriaGKCentral", "MGI / Austria GK Central");
        public static readonly CrsCandidate AtGkEast = new CrsCandidate(31256, "AustriaGKEast", "MGI / Austria GK East");
        public static readonly CrsCandidate AtM28 = new CrsCandidate(31257, "AustriaM28", "MGI / Austria GK M28 (BMN)");
        public static readonly CrsCandidate AtM31 = new CrsCandidate(31258, "AustriaM31", "MGI / Austria GK M31 (BMN)");
        public static readonly CrsCandidate AtM34 = new CrsCandidate(31259, "AustriaM34", "MGI / Austria GK M34 (BMN)");
        public static readonly CrsCandidate AtLambert = new CrsCandidate(31287, "AustriaLambert", "MGI / Austria Lambert");
        public static readonly CrsCandidate AtLambertEtrs = new CrsCandidate(3416, "AustriaLambertETRS89", "ETRS89 / Austria Lambert");
        public static readonly CrsCandidate Utm31 = new CrsCandidate(25831, "UTM31", "ETRS89 / UTM zone 31N");
        public static readonly CrsCandidate Utm32 = new CrsCandidate(25832, "UTM32", "ETRS89 / UTM zone 32N");
        public static readonly CrsCandidate Utm33 = new CrsCandidate(25833, "UTM33", "ETRS89 / UTM zone 33N");

        static readonly CrsCandidate[] All =
        {
            Utm32, Utm33, Utm31, LV95, LV03, AtGkWest, AtGkCentral, AtGkEast, AtM28, AtM31, AtM34, AtLambert, AtLambertEtrs
        };

        /// <summary>Coordinates far from the origin are map coordinates (local models stay within 100 km)</summary>
        public static bool LooksLikeMap(double e, double n) => Math.Abs(e) > 100000 || Math.Abs(n) > 100000;

        public static CrsGuess Guess(double e, double n, int defaultUtm)
        {
            var g = new CrsGuess();
            // UTM with zone prefix (8-digit eastings: 32 373 064)
            if (e >= 1e7 && e < 1e8 && n >= 3e6 && n < 9.4e6)
            {
                int zone = (int)Math.Floor(e / 1e6);
                if (zone == 31) { g.Epsg = 5649; g.Region = "UTM zone 31 with zone prefix"; return g; }
                if (zone == 32) { g.Epsg = 4647; g.Region = "UTM zone 32 with zone prefix"; return g; }
                if (zone == 33) { g.Epsg = 5650; g.Region = "UTM zone 33 with zone prefix"; return g; }
            }
            // Swiss LV95: E 2 480 000–2 840 000, N 1 070 000–1 300 000
            if (e >= 2.4e6 && e < 2.9e6 && n >= 1.0e6 && n < 1.4e6) { g.Epsg = LV95.Epsg; g.Region = "Switzerland (LV95)"; return g; }
            // German Gauss-Krüger with zone prefix 2–5: N 5.2–6.1 Mio
            if (e >= 2e6 && e < 6e6 && n >= 5e6 && n < 6.2e6)
            {
                int zone = (int)Math.Floor(e / 1e6);
                g.Epsg = 31464 + zone;
                g.Region = "Gauss-Krüger zone " + zone;
                return g;
            }
            // UTM without zone prefix: which zone cannot be told from the numbers
            if (e >= 1.6e5 && e < 8.4e5 && n >= 4e6 && n < 7e6)
            {
                g.Epsg = defaultUtm;
                g.Region = "UTM without zone prefix";
                return g;
            }
            // Swiss LV03 (E 480 000–840 000, N 70 000–300 000) overlaps Austrian BMN and Lambert
            if (e >= 4.8e5 && e < 8.5e5 && n >= 6e4 && n < 3.1e5)
            {
                g.Region = "Switzerland (LV03) or Austria";
                g.Candidates.AddRange(new[] { LV03, AtM31, AtM34, AtLambert, AtLambertEtrs });
                return g;
            }
            // Austrian Gauss-Krüger West/Central/East (false easting 0) or BMN M28
            if (e > -2e5 && e < 2e5 && n >= 1e5 && n < 4.5e5)
            {
                g.Region = "Austria (Gauss-Krüger)";
                g.Candidates.AddRange(new[] { AtGkCentral, AtGkWest, AtGkEast, AtM28 });
                return g;
            }
            // Austria Lambert or BMN
            if (e >= 1e5 && e < 9e5 && n >= 1e5 && n < 6.2e5)
            {
                g.Region = "Austria";
                g.Candidates.AddRange(new[] { AtLambert, AtLambertEtrs, AtM31, AtM34, AtM28 });
                return g;
            }
            g.Region = "unknown";
            g.Candidates.AddRange(All);
            return g;
        }

        public static CrsCandidate ByOption(string option) =>
            All.FirstOrDefault(c => string.Equals(c.Option, option, StringComparison.OrdinalIgnoreCase));
    }
}
