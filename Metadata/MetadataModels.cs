namespace PgDataHub.Metadata;

public sealed class CollectionMetadata
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";

    public bool IsSpatial { get; set; }

    public string? GeometryColumn { get; set; }
    public string? GeometryType { get; set; }
    public int? Srid { get; set; }

    public string? PrimaryKey { get; set; }

    public List<ColumnMetadata> Columns { get; set; } = [];

    internal IReadOnlyList<ColumnMetadata>? PolicyColumns { get; set; }
    internal IReadOnlyList<IReadOnlyList<PgDataHub.Features.PropertyFilter>>? RowFilters { get; set; }
    internal CollectionMetadata Copy() => (CollectionMetadata)MemberwiseClone();
}

public sealed class ColumnMetadata
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Nullable { get; set; }
}

public enum FunctionReturnType
{
    Rowset,
    Scalar,
    Json,
    Composite,
    Array
}

public sealed class FunctionMetadata
{
    public uint Oid { get; set; }

    public string Schema { get; set; } = "";

    public string Name { get; set; } = "";

    public string Signature { get; set; } = "";

    public string Description { get; set; } = "";

    public FunctionReturnType ReturnType { get; set; }

    public FunctionVolatility Volatility { get; set; } = FunctionVolatility.Volatile;

    public bool IsVolatile => Volatility == FunctionVolatility.Volatile;

    public List<FunctionParameterMetadata> Parameters { get; set; } = [];

    public List<FunctionColumnMetadata> Columns { get; set; } = [];
}

public enum FunctionVolatility { Volatile, Stable, Immutable }

public sealed class FunctionParameterMetadata
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";
}

public sealed class FunctionColumnMetadata
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";
}
