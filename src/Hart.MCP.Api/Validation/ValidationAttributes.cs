using System.ComponentModel.DataAnnotations;

namespace Hart.MCP.Api.Validation;

/// <summary>
/// Validates that a GUID is not empty
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public class NonEmptyGuidAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is Guid guid && guid == Guid.Empty)
        {
            return new ValidationResult(ErrorMessage ?? "GUID cannot be empty");
        }
        return ValidationResult.Success;
    }
}

/// <summary>
/// Validates that a collection has at least a minimum number of items
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public class MinCollectionCountAttribute : ValidationAttribute
{
    private readonly int _minCount;

    public MinCollectionCountAttribute(int minCount)
    {
        _minCount = minCount;
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is System.Collections.ICollection collection && collection.Count < _minCount)
        {
            return new ValidationResult(ErrorMessage ?? $"Collection must have at least {_minCount} items");
        }
        return ValidationResult.Success;
    }
}

/// <summary>
/// Validates that a spatial coordinate is within valid bounds
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public class SpatialCoordinateAttribute : ValidationAttribute
{
    private readonly double _min;
    private readonly double _max;

    public SpatialCoordinateAttribute(double min = -1e10, double max = 1e10)
    {
        _min = min;
        _max = max;
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                return new ValidationResult(ErrorMessage ?? "Coordinate cannot be NaN or Infinity");
            }
            if (d < _min || d > _max)
            {
                return new ValidationResult(ErrorMessage ?? $"Coordinate must be between {_min} and {_max}");
            }
        }
        return ValidationResult.Success;
    }
}

/// <summary>
/// Validates content size limits
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public class MaxContentSizeAttribute : ValidationAttribute
{
    private readonly int _maxBytes;

    public MaxContentSizeAttribute(int maxBytes = 10 * 1024 * 1024) // 10MB default
    {
        _maxBytes = maxBytes;
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is string s && s.Length * 2 > _maxBytes) // UTF-16 estimate
        {
            return new ValidationResult(ErrorMessage ?? $"Content exceeds maximum size of {_maxBytes / 1024 / 1024}MB");
        }
        if (value is byte[] bytes && bytes.Length > _maxBytes)
        {
            return new ValidationResult(ErrorMessage ?? $"Content exceeds maximum size of {_maxBytes / 1024 / 1024}MB");
        }
        return ValidationResult.Success;
    }
}
