namespace Bunting.Evaluation;

internal sealed class CompiledSegment
{
    private HashSet<string> _included = [];
    private HashSet<string> _excluded = [];
    private CompiledCondition[] _conditions = [];

    public void Initialize(IEnumerable<string> included, IEnumerable<string> excluded, CompiledCondition[] conditions)
    {
        _included = new HashSet<string>(included, StringComparer.Ordinal);
        _excluded = new HashSet<string>(excluded, StringComparer.Ordinal);
        _conditions = conditions;
    }

    /// <summary>Excluded wins over included; then conditions (AND) decide. A segment without conditions only has explicit members.</summary>
    public bool IsMember(EvaluationContext context, int depth)
    {
        if (depth > CompiledCondition.SegmentDepthLimit)
        {
            return false;
        }

        var key = context.TargetingKey;
        if (key is not null)
        {
            if (_excluded.Contains(key))
            {
                return false;
            }

            if (_included.Contains(key))
            {
                return true;
            }
        }

        if (_conditions.Length == 0)
        {
            return false;
        }

        foreach (var condition in _conditions)
        {
            if (!condition.Evaluate(context, depth))
            {
                return false;
            }
        }

        return true;
    }
}
