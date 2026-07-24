namespace AuthorityLab;

public sealed record RuntimeWorkRequest(
    string WorkId,
    string WorkClass,
    string Criticality,
    int FullCostUnits,
    int ReducedCostUnits,
    bool CanDefer,
    string PreferredTransport,
    string FallbackTransport,
    int Priority);

public sealed record RuntimeGateDecision(
    RuntimeWorkRequest Request,
    string SelectedMode,
    int SelectedCostUnits,
    string Reason,
    string Transport,
    int BudgetRemainingUnits,
    int DeferredDepth);

public static class RuntimeEnvelopePolicy
{
    public static IReadOnlyList<RuntimeGateDecision> Evaluate(
        IEnumerable<RuntimeWorkRequest> requests,
        int budgetUnits,
        int deferredCapacity)
    {
        if (budgetUnits < 0) throw new ArgumentOutOfRangeException(nameof(budgetUnits));
        if (deferredCapacity < 0) throw new ArgumentOutOfRangeException(nameof(deferredCapacity));

        var remaining = budgetUnits;
        var deferredDepth = 0;
        var decisions = new List<RuntimeGateDecision>();

        foreach (var request in requests
                     .OrderByDescending(item => item.Priority)
                     .ThenBy(item => item.WorkId, StringComparer.Ordinal))
        {
            if (request.FullCostUnits <= 0 ||
                request.ReducedCostUnits <= 0 ||
                request.ReducedCostUnits > request.FullCostUnits)
                throw new ArgumentException($"invalid costs for {request.WorkId}", nameof(requests));

            var protectedWork = request.Criticality.Equals("critical", StringComparison.Ordinal);
            string selectedMode;
            string reason;
            string transport;
            int selectedCost;

            if (protectedWork)
            {
                selectedMode = "full";
                selectedCost = request.FullCostUnits;
                reason = selectedCost <= remaining ? "critical_work_preserved" : "critical_budget_overrun";
                transport = request.PreferredTransport;
                remaining -= selectedCost;
            }
            else if (request.FullCostUnits <= remaining)
            {
                selectedMode = "full";
                selectedCost = request.FullCostUnits;
                reason = "budget_available";
                transport = request.PreferredTransport;
                remaining -= selectedCost;
            }
            else if (request.ReducedCostUnits <= remaining)
            {
                selectedMode = "reduced";
                selectedCost = request.ReducedCostUnits;
                reason = "full_cost_exceeds_budget";
                transport = request.PreferredTransport;
                remaining -= selectedCost;
            }
            else if (request.CanDefer && deferredDepth < deferredCapacity)
            {
                selectedMode = "deferred";
                selectedCost = 0;
                reason = "budget_exhausted_queue_bounded";
                transport = "none";
                deferredDepth++;
            }
            else
            {
                selectedMode = "dropped";
                selectedCost = 0;
                reason = request.CanDefer ? "deferred_capacity_exhausted" : "non_deferrable_budget_exhausted";
                transport = "none";
            }

            decisions.Add(new RuntimeGateDecision(
                request,
                selectedMode,
                selectedCost,
                reason,
                transport,
                remaining,
                deferredDepth));
        }

        return decisions;
    }
}
