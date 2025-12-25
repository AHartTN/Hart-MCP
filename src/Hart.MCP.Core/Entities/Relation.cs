namespace Hart.MCP.Core.Entities;

/// <summary>
/// A relation - an edge in the composition graph.
/// Links a parent composition to a child (constant or composition).
/// Position defines order; Multiplicity enables RLE compression.
/// </summary>
public class Relation
{
    public long Id { get; set; }

    /// <summary>
    /// Parent composition that owns this relation.
    /// </summary>
    public long CompositionId { get; set; }
    public virtual Composition Composition { get; set; } = null!;

    /// <summary>
    /// Child constant (mutually exclusive with ChildCompositionId).
    /// </summary>
    public long? ChildConstantId { get; set; }
    public virtual Constant? ChildConstant { get; set; }

    /// <summary>
    /// Child composition (mutually exclusive with ChildConstantId).
    /// </summary>
    public long? ChildCompositionId { get; set; }
    public virtual Composition? ChildComposition { get; set; }

    /// <summary>
    /// Order within the parent composition (0-indexed).
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Run-length encoding multiplicity (default 1).
    /// Value of N means this child repeats N times at this position.
    /// </summary>
    public int Multiplicity { get; set; } = 1;
}
