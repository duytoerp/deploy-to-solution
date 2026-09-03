namespace DeployToSolution.Models
{
    public enum RowState
    {
        Pending,
        Resolved,
        Added,
        AlreadyIn,
        Ambiguous,
        NotFound,
        Failed,
        Skipped
    }
}
