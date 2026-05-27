namespace TroubleScout.Services;

public sealed record AutoCommandApprovalDecision(
    bool IsReadOnly,
    string Model,
    string Rationale);

public interface IAutoCommandApprovalEvaluator
{
    Task<AutoCommandApprovalDecision?> EvaluateAsync(string command, CancellationToken cancellationToken = default);
}
