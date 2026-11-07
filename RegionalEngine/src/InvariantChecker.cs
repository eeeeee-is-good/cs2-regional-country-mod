using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    public sealed class InvariantViolation
    {
        public InvariantViolation(string invariant, string? destCityId, string? resourceId, string message)
        {
            Invariant = invariant; DestCityId = destCityId; ResourceId = resourceId; Message = message;
        }

        /// <summary>One of the InvariantChecker constants, e.g. "18.4#7".</summary>
        public string Invariant { get; }
        public string? DestCityId { get; }
        public string? ResourceId { get; }
        public string Message { get; }

        public override string ToString() =>
            DestCityId == null ? $"[{Invariant}] {Message}" : $"[{Invariant}] {DestCityId}/{ResourceId}: {Message}";
    }

    public sealed class InvariantReport
    {
        public List<InvariantViolation> Violations { get; } = new List<InvariantViolation>();
        public int QuotesChecked { get; internal set; }
        public bool Passed => Violations.Count == 0;
    }

    /// <summary>
    /// Runs every (destination city, resource) quote for a region and checks the hard invariants of log §18.4
    /// that apply to the quote layer:
    ///   #1 graph isolation (counterfactual: boosting an unreachable supplier changes nothing),
    ///   #2 path correctness (contiguous, directional, usable, mode-compatible, freight = sum of links),
    ///   #3 best feasible offer (removing any link never makes an offer cheaper or newly eligible),
    ///   #7 numerical safety (finite, non-negative, components sum to landedPrice),
    ///   #14 evidence labeling (modeled quote, not applied to the game).
    /// #8 (idempotent snapshots) is a region-store property and is tested with RegionStore (A2).
    /// </summary>
    public sealed class InvariantChecker
    {
        public const string GraphIsolation = "18.4#1";
        public const string PathCorrectness = "18.4#2";
        public const string BestFeasibleOffer = "18.4#3";
        public const string NumericSafety = "18.4#7";
        public const string EvidenceLabeling = "18.4#14";
        public const string Selection = "selection";

        private readonly IRegionalEngine _engine;

        public InvariantChecker(IRegionalEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        public InvariantReport Check(RegionState state)
        {
            var report = new InvariantReport();
            var problems = RegionStateValidator.Validate(state);
            if (problems.Count > 0)
            {
                foreach (var p in problems) report.Violations.Add(new InvariantViolation(NumericSafety, null, null, "invalid input: " + p));
                return report;
            }

            var m = state.Manifest;
            var resourceIds = m.Parameters.ResourcePrices.Select(r => r.ResourceId)
                .Concat(state.LatestSnapshots.SelectMany(s => s.Snapshot.Resources.Select(r => r.ResourceId)))
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            foreach (var dest in m.Cities.Select(c => c.CityId).OrderBy(x => x, StringComparer.Ordinal))
            foreach (var resourceId in resourceIds)
            {
                var request = new QuoteRequest { DestCityId = dest, ResourceId = resourceId };
                var q = TryQuote(state, request, report);
                if (q == null) continue;
                report.QuotesChecked++;

                void Fail(string invariant, string message) => report.Violations.Add(new InvariantViolation(invariant, dest, resourceId, message));

                CheckLabeling(q, Fail);
                CheckNumbers(q, Fail);
                CheckSelection(q, Fail);
                CheckPaths(state, q, Fail);
                CheckIsolation(state, request, q, report, Fail);
                CheckLinkRemoval(state, request, q, report, Fail);
            }
            return report;
        }

        private QuoteResult? TryQuote(RegionState state, QuoteRequest request, InvariantReport report)
        {
            try { return _engine.Quote(state, request); }
            catch (RegionStateException ex)
            {
                report.Violations.Add(new InvariantViolation(NumericSafety, request.DestCityId, request.ResourceId, ex.Message));
                return null;
            }
        }

        private static void CheckLabeling(QuoteResult q, Action<string, string> fail)
        {
            if (q.ReportedAs != ReportedAs.ModeledPriceQuoteNotPhysicalTrade) fail(EvidenceLabeling, $"reportedAs is {q.ReportedAs}");
            if (q.AppliedToGame) fail(EvidenceLabeling, "appliedToGame is true, but no game writer exists");
            if (string.IsNullOrEmpty(q.Provenance.TraceId)) fail(EvidenceLabeling, "quote has no traceId");
        }

        private static void CheckNumbers(QuoteResult q, Action<string, string> fail)
        {
            for (var i = 0; i < q.Offers.Count; i++)
            {
                var o = q.Offers[i];
                if (o.EffectiveRegionalOfferPerGameDay is double e && !Num.IsFinite(e)) fail(NumericSafety, $"offer {i}: effective offer is not finite");
                if (!o.Eligible)
                {
                    if (o.ExcludedReason == null) fail(NumericSafety, $"offer {i}: ineligible without a reason");
                    if (o.LandedPrice != null) fail(NumericSafety, $"offer {i}: ineligible offer has a landed price");
                    continue;
                }
                var parts = new[] { o.ProducerPrice, o.Freight, o.Tariff, o.Subsidy, o.LandedPrice };
                if (parts.Any(p => p == null || !Num.IsFiniteNonNegative(p.Value)))
                {
                    fail(NumericSafety, $"offer {i}: a price component is null, negative or not finite");
                    continue;
                }
                var sum = o.ProducerPrice!.Value + o.Freight!.Value + o.Tariff!.Value - o.Subsidy!.Value;
                if (!Close(sum, o.LandedPrice!.Value)) fail(NumericSafety, $"offer {i}: components sum to {F(sum)}, landedPrice is {F(o.LandedPrice.Value)}");
            }
        }

        private static void CheckSelection(QuoteResult q, Action<string, string> fail)
        {
            int? best = null;
            for (var i = 0; i < q.Offers.Count; i++)
                if (q.Offers[i].Eligible && q.Offers[i].LandedPrice != null
                    && (best == null || q.Offers[i].LandedPrice < q.Offers[best.Value].LandedPrice)) best = i;
            if (best != q.SelectedOfferIndex) fail(Selection, $"selected offer {q.SelectedOfferIndex?.ToString() ?? "none"}, cheapest eligible is {best?.ToString() ?? "none"}");
        }

        private static void CheckPaths(RegionState state, QuoteResult q, Action<string, string> fail)
        {
            var m = state.Manifest;
            var links = m.Links.ToDictionary(l => l.LinkId, StringComparer.Ordinal);
            var costs = m.Parameters.ModeCosts.ToDictionary(c => c.Mode);
            var latest = state.LatestSnapshots.ToDictionary(s => s.Snapshot.CityId, StringComparer.Ordinal);
            var allowed = state.Resources.FirstOrDefault(r => r.ResourceId == q.ResourceId)?.AllowedFreightModes ?? new TransportMode[0];

            for (var i = 0; i < q.Offers.Count; i++)
            {
                var o = q.Offers[i];
                if (o.OriginKind != OriginKind.City || !o.Eligible) continue;
                var at = o.OriginCityId ?? "";
                var visited = new HashSet<string>(StringComparer.Ordinal) { at };
                var freight = 0.0;
                var ok = true;
                foreach (var id in o.Path)
                {
                    if (!links.TryGetValue(id, out var l)) { fail(PathCorrectness, $"offer {i}: path uses unknown link {id}"); ok = false; break; }
                    if (!RouteGraph.LinkUsable(l, costs, latest)) { fail(PathCorrectness, $"offer {i}: path uses unusable link {id}"); ok = false; break; }
                    if (!allowed.Contains(l.Mode)) { fail(PathCorrectness, $"offer {i}: link {id} mode {l.Mode} is not allowed for {q.ResourceId}"); ok = false; break; }
                    if (l.A.CityId == at) at = l.B.CityId;
                    else if (l.Bidirectional && l.B.CityId == at) at = l.A.CityId;
                    else { fail(PathCorrectness, $"offer {i}: path breaks at link {id}"); ok = false; break; }
                    if (!visited.Add(at)) { fail(PathCorrectness, $"offer {i}: path revisits {at}"); ok = false; break; }
                    freight += costs[l.Mode].FixedCost + costs[l.Mode].CostPerKm * l.DistanceKm;
                }
                if (!ok) continue;
                if (at != q.DestCityId) fail(PathCorrectness, $"offer {i}: path ends at {at}, not {q.DestCityId}");
                else if (o.Freight == null || !Close(freight, o.Freight.Value)) fail(PathCorrectness, $"offer {i}: freight {F(o.Freight)} but path links sum to {F(freight)}");
            }
        }

        /// <summary>#1: an unreachable supplier with 10x more steel must not change anything.</summary>
        private void CheckIsolation(RegionState state, QuoteRequest request, QuoteResult q, InvariantReport report, Action<string, string> fail)
        {
            foreach (var o in q.Offers.Where(o => o.OriginKind == OriginKind.City
                         && (o.ExcludedReason == ExcludedReason.NoPath || o.ExcludedReason == ExcludedReason.ModeIncompatible)))
            {
                var boosted = WithBoostedSupplier(state, o.OriginCityId!, request.ResourceId);
                var q2 = TryQuote(boosted, request, report);
                if (q2 != null && Decision(q2) != Decision(q))
                    fail(GraphIsolation, $"boosting unreachable supplier {o.OriginCityId} changed the quote");
            }
        }

        /// <summary>#3, checked in reverse: removing any link never makes an offer cheaper or newly eligible.</summary>
        private void CheckLinkRemoval(RegionState state, QuoteRequest request, QuoteResult q, InvariantReport report, Action<string, string> fail)
        {
            foreach (var link in state.Manifest.Links.OrderBy(l => l.LinkId, StringComparer.Ordinal))
            {
                var q2 = TryQuote(WithoutLink(state, link.LinkId), request, report);
                if (q2 == null) continue;
                if (q2.Offers.Count != q.Offers.Count) { fail(BestFeasibleOffer, $"removing link {link.LinkId} changed the set of offers"); continue; }
                for (var i = 0; i < q.Offers.Count; i++)
                {
                    var before = q.Offers[i];
                    var after = q2.Offers[i];
                    if (after.Eligible && !before.Eligible)
                        fail(BestFeasibleOffer, $"removing link {link.LinkId} made offer {i} eligible");
                    else if (after.Eligible && before.Eligible && after.LandedPrice < before.LandedPrice - Tolerance(before.LandedPrice!.Value))
                        fail(BestFeasibleOffer, $"removing link {link.LinkId} made offer {i} cheaper ({F(before.LandedPrice)} -> {F(after.LandedPrice)})");
                }
            }
        }

        private static RegionState WithoutLink(RegionState s, string linkId)
        {
            var m = s.Manifest;
            var copy = new RegionManifest
            {
                SchemaVersion = m.SchemaVersion, RegionId = m.RegionId, Revision = m.Revision,
                Cities = m.Cities, WorldGateways = m.WorldGateways, Parameters = m.Parameters,
                Links = m.Links.Where(l => !string.Equals(l.LinkId, linkId, StringComparison.Ordinal)).ToList(),
            };
            return new RegionState(copy, s.LatestSnapshots, s.CurrentRegionSeq, s.Resources);
        }

        private static RegionState WithBoostedSupplier(RegionState s, string cityId, string resourceId)
        {
            var snapshots = s.LatestSnapshots.Select(p =>
            {
                if (!string.Equals(p.Snapshot.CityId, cityId, StringComparison.Ordinal)) return p;
                var o = p.Snapshot;
                var copy = new CitySnapshot
                {
                    SchemaVersion = o.SchemaVersion, CityId = o.CityId, Seq = o.Seq, ParentSeq = o.ParentSeq,
                    Producer = o.Producer, Profiles = o.Profiles, GameTime = o.GameTime,
                    MeasurementWindowGameHours = o.MeasurementWindowGameHours,
                    OutsideConnections = o.OutsideConnections, ExportedAtUtc = o.ExportedAtUtc,
                    Resources = o.Resources.Select(r => !string.Equals(r.ResourceId, resourceId, StringComparison.Ordinal) ? r : new ResourceObservation
                    {
                        ResourceId = r.ResourceId,
                        RealizedOutputPerGameDay = (r.RealizedOutputPerGameDay ?? 0) * 10 + 1000,
                        InputUsePerGameDay = 0,
                        CapacityEstimatePerGameDay = (r.CapacityEstimatePerGameDay ?? 0) * 10 + 1000,
                        ProducerCompanyCount = r.ProducerCompanyCount,
                        Confidence = Confidence.Validated,
                        Source = r.Source,
                    }).ToList(),
                };
                return new PublishedSnapshot(copy, p.RegionSeq);
            });
            return new RegionState(s.Manifest, snapshots, s.CurrentRegionSeq, s.Resources);
        }

        /// <summary>Offers and selection, excluding the supplier snapshot's own figures (they change in the counterfactual).</summary>
        private static string Decision(QuoteResult q) =>
            string.Join("|", q.Offers.Select(o => string.Join(",",
                o.OriginKind, o.OriginCityId, o.WorldGatewayEndpointKey, string.Join(">", o.Path), o.Eligible, o.ExcludedReason,
                F(o.ProducerPrice), F(o.Freight), F(o.Tariff), F(o.Subsidy), F(o.LandedPrice))))
            + "|selected=" + q.SelectedOfferIndex;

        private static bool Close(double a, double b) => Math.Abs(a - b) <= Tolerance(Math.Max(Math.Abs(a), Math.Abs(b)));
        private static double Tolerance(double magnitude) => 1e-9 * Math.Max(1, Math.Abs(magnitude));
        private static string F(double? d) => d?.ToString("R", CultureInfo.InvariantCulture) ?? "null";
    }
}
