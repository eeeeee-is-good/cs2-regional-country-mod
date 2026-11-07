using System;
using System.Collections.Generic;
using System.Linq;
using Regions.Contracts;

namespace Regions.Engine
{
    /// <summary>
    /// Structural and numeric checks the engine needs before it can quote safely (log §18.4 #7:
    /// no NaN, infinity or negative distance/cost). Schema validation is the store's job; this
    /// re-checks only what would make the engine's arithmetic or graph wrong.
    /// </summary>
    public static class RegionStateValidator
    {
        public static List<string> Validate(RegionState state)
        {
            var p = new List<string>();
            var m = state.Manifest;

            if (m.Cities == null || m.Links == null || m.WorldGateways == null || m.Parameters == null
                || m.Parameters.ModeCosts == null || m.Parameters.ResourcePrices == null || m.Parameters.Staleness == null)
            {
                p.Add("manifest: a required list or object is null");
                return p;
            }

            var cityIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in m.Cities)
            {
                if (string.IsNullOrEmpty(c.CityId)) p.Add("cities: empty cityId");
                else if (!cityIds.Add(c.CityId)) p.Add($"cities[{c.CityId}]: duplicate cityId");
            }

            var linkIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in m.Links)
            {
                var id = l.LinkId ?? "";
                if (id.Length == 0) p.Add("links: empty linkId");
                else if (!linkIds.Add(id)) p.Add($"links[{id}]: duplicate linkId");
                if (l.A == null || l.B == null) { p.Add($"links[{id}]: missing endpoint"); continue; }
                if (!cityIds.Contains(l.A.CityId)) p.Add($"links[{id}]: unknown city {l.A.CityId}");
                if (!cityIds.Contains(l.B.CityId)) p.Add($"links[{id}]: unknown city {l.B.CityId}");
                if (string.Equals(l.A.CityId, l.B.CityId, StringComparison.Ordinal)) p.Add($"links[{id}]: both ends in the same city");
                if (!Num.IsFiniteNonNegative(l.DistanceKm)) p.Add($"links[{id}]: distanceKm must be finite and >= 0, got {l.DistanceKm}");
            }

            var gateways = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in m.WorldGateways)
            {
                var key = g.CityId + "/" + g.EndpointKey;
                if (!cityIds.Contains(g.CityId)) p.Add($"worldGateways[{key}]: unknown city");
                if (!gateways.Add(key)) p.Add($"worldGateways[{key}]: duplicate");
                if (!Num.IsFiniteNonNegative(g.DistanceKm)) p.Add($"worldGateways[{key}]: distanceKm must be finite and >= 0, got {g.DistanceKm}");
            }

            foreach (var group in m.Parameters.ModeCosts.GroupBy(c => c.Mode).Where(g => g.Count() > 1))
                p.Add($"modeCosts[{group.Key}]: duplicate mode");
            foreach (var c in m.Parameters.ModeCosts)
            {
                if (!Num.IsFiniteNonNegative(c.FixedCost)) p.Add($"modeCosts[{c.Mode}]: fixedCost must be finite and >= 0");
                if (!Num.IsFiniteNonNegative(c.CostPerKm)) p.Add($"modeCosts[{c.Mode}]: costPerKm must be finite and >= 0");
            }

            var priceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in m.Parameters.ResourcePrices)
            {
                if (!priceIds.Add(r.ResourceId)) p.Add($"resourcePrices[{r.ResourceId}]: duplicate");
                if (!Num.IsFiniteNonNegative(r.RegionalProducerPriceIndex)) p.Add($"resourcePrices[{r.ResourceId}]: regionalProducerPriceIndex must be finite and >= 0");
                if (!Num.IsFiniteNonNegative(r.WorldProducerPriceIndex)) p.Add($"resourcePrices[{r.ResourceId}]: worldProducerPriceIndex must be finite and >= 0");
            }

            ValidatePolicies(m, p);
            if (m.Parameters.Scarcity is ScarcityRule sr)
            {
                if (!Num.IsFiniteNonNegative(sr.PremiumSlope)) p.Add("scarcity.premiumSlope must be finite and >= 0");
                if (!Num.IsFiniteNonNegative(sr.MaxPremium)) p.Add("scarcity.maxPremium must be finite and >= 0");
            }

            if (m.Parameters.Staleness.MaxRegionSeqAge < 0) p.Add("staleness.maxRegionSeqAge must be >= 0 or null");
            if (state.CurrentRegionSeq < 0) p.Add("currentRegionSeq must be >= 0");

            var snapshotCities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pub in state.LatestSnapshots)
            {
                var s = pub.Snapshot;
                var id = s.CityId ?? "";
                if (!snapshotCities.Add(id)) p.Add($"snapshots[{id}]: more than one latest snapshot for this city");
                if (!cityIds.Contains(id)) p.Add($"snapshots[{id}]: city is not in the manifest");
                if (pub.RegionSeq < 1 || pub.RegionSeq > state.CurrentRegionSeq)
                    p.Add($"snapshots[{id}]: regionSeq {pub.RegionSeq} outside 1..{state.CurrentRegionSeq}");
                if (s.Resources == null || s.OutsideConnections == null) { p.Add($"snapshots[{id}]: null list"); continue; }

                var resIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in s.Resources)
                {
                    if (!resIds.Add(r.ResourceId)) p.Add($"snapshots[{id}].resources[{r.ResourceId}]: duplicate");
                    if (!Num.IsFiniteNonNegative(r.RealizedOutputPerGameDay)) p.Add($"snapshots[{id}].resources[{r.ResourceId}]: realizedOutputPerGameDay must be null or finite >= 0");
                    if (!Num.IsFiniteNonNegative(r.InputUsePerGameDay)) p.Add($"snapshots[{id}].resources[{r.ResourceId}]: inputUsePerGameDay must be null or finite >= 0");
                    if (!Num.IsFiniteNonNegative(r.CapacityEstimatePerGameDay)) p.Add($"snapshots[{id}].resources[{r.ResourceId}]: capacityEstimatePerGameDay must be null or finite >= 0");
                }

                var ocKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var oc in s.OutsideConnections)
                    if (!ocKeys.Add(oc.EndpointKey)) p.Add($"snapshots[{id}].outsideConnections[{oc.EndpointKey}]: duplicate endpointKey");
            }

            var defIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in state.Resources)
                if (!defIds.Add(d.ResourceId)) p.Add($"resources[{d.ResourceId}]: duplicate definition");

            return p;
        }

        private static void ValidatePolicies(RegionManifest m, List<string> p)
        {
            var policies = m.Parameters.Policies;
            if (policies == null) return; // a missing optional block means no policies
            if (policies.ImportTariffs == null || policies.FreightSubsidies == null) { p.Add("policies: a list is null"); return; }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var tariffResources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in policies.ImportTariffs)
            {
                var id = t.PolicyId ?? "";
                if (id.Length == 0 || !ids.Add(id)) p.Add($"policies[{id}]: empty or duplicate policyId");
                if (!tariffResources.Add(t.ResourceId)) p.Add($"policies[{id}]: more than one import tariff for {t.ResourceId}");
                if (!Num.IsFiniteNonNegative(t.AdValoremPercent)) p.Add($"policies[{id}]: adValoremPercent must be finite and >= 0");
                if (!Num.IsFiniteNonNegative(t.SpecificPerUnit)) p.Add($"policies[{id}]: specificPerUnit must be finite and >= 0");
            }

            var links = new Dictionary<string, LinkEntry>(StringComparer.Ordinal);
            foreach (var l in m.Links) if (l.LinkId != null && !links.ContainsKey(l.LinkId)) links[l.LinkId] = l;
            var subsidyKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in policies.FreightSubsidies)
            {
                var id = s.PolicyId ?? "";
                if (id.Length == 0 || !ids.Add(id)) p.Add($"policies[{id}]: empty or duplicate policyId");
                if (!subsidyKeys.Add(s.ResourceId + "\u0000" + s.Mode)) p.Add($"policies[{id}]: more than one freight subsidy for {s.ResourceId} by {s.Mode}");
                if (!Num.IsFinite(s.PercentOfFreight) || s.PercentOfFreight < 0 || s.PercentOfFreight > 100) p.Add($"policies[{id}]: percentOfFreight must be between 0 and 100");
                if (!Num.IsFiniteNonNegative(s.BudgetPerGameDay)) p.Add($"policies[{id}]: budgetPerGameDay must be finite and >= 0");
                if (s.LinkIds == null) continue;
                if (s.LinkIds.Distinct(StringComparer.Ordinal).Count() != s.LinkIds.Count) p.Add($"policies[{id}]: duplicate linkIds");
                foreach (var linkId in s.LinkIds)
                {
                    if (!links.TryGetValue(linkId, out var link)) p.Add($"policies[{id}]: unknown link {linkId}");
                    else if (link.Mode != s.Mode) p.Add($"policies[{id}]: link {linkId} is {link.Mode}, the subsidy is for {s.Mode}");
                }
            }
        }
    }
}
