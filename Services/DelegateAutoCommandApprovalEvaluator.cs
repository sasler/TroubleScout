namespace TroubleScout.Services;

internal sealed class DelegateAutoCommandApprovalEvaluator(
    Func<string, CancellationToken, Task<AutoCommandApprovalDecision?>> evaluate) : IAutoCommandApprovalEvaluator
{
    public Task<AutoCommandApprovalDecision?> EvaluateAsync(string command, CancellationToken cancellationToken = default) =>
        evaluate(command, cancellationToken);
}
