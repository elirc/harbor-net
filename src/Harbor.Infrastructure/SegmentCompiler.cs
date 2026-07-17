using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Infrastructure;

/// <summary>Raised when a rule set cannot be turned into a query.</summary>
public class SegmentRuleException(string message) : Exception(message);

/// <summary>
/// Turns a segment's stored rules into an Expression the database can run.
///
/// The point of compiling to an expression rather than evaluating in memory is
/// that membership must stay a query: a segment over a million contacts is a
/// WHERE clause, not a million objects. Custom attributes reach into the JSON
/// column through SQLite's json_extract, so they filter server-side too.
///
/// Every condition is built against the same parameter, so combining them is a
/// plain AndAlso/OrElse with no parameter rebinding.
/// </summary>
public static class SegmentCompiler
{
    private static readonly MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    private static readonly MethodInfo StartsWithMethod =
        typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    private static readonly MethodInfo EndsWithMethod =
        typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    private static readonly MethodInfo JsonExtractMethod =
        typeof(HarborDbContext).GetMethod(nameof(HarborDbContext.JsonExtract))!;

    /// <summary>
    /// Compiles the rules into a predicate over contacts.
    /// </summary>
    /// <exception cref="SegmentRuleException">The rules are unusable.</exception>
    public static Expression<Func<Contact, bool>> Compile(SegmentRuleSet rules)
    {
        if (rules.Conditions.Count == 0)
        {
            throw new SegmentRuleException("A segment needs at least one condition.");
        }

        var parameter = Expression.Parameter(typeof(Contact), "c");
        var conditions = rules.Conditions.Select(c => Build(c, parameter)).ToList();

        var body = rules.Match == SegmentMatch.Any
            ? conditions.Aggregate(Expression.OrElse)
            : conditions.Aggregate(Expression.AndAlso);

        return Expression.Lambda<Func<Contact, bool>>(body, parameter);
    }

    /// <summary>Validates rules without building a query.</summary>
    public static void Validate(SegmentRuleSet rules) => Compile(rules);

    private static Expression Build(SegmentCondition condition, ParameterExpression parameter)
    {
        var field = condition.Field.Trim().ToLowerInvariant();

        if (SegmentFields.IsDateField(field))
        {
            return BuildDate(field, condition, parameter);
        }

        return BuildText(field, condition, parameter);
    }

    private static Expression BuildText(
        string field, SegmentCondition condition, ParameterExpression parameter)
    {
        var text = TextField(field, parameter);

        // Exists/NotExists are the only operators that need no value, and they
        // are answered purely by nullness.
        switch (condition.Operator)
        {
            case SegmentOperator.Exists:
                return Expression.NotEqual(text, Expression.Constant(null, typeof(string)));
            case SegmentOperator.NotExists:
                return Expression.Equal(text, Expression.Constant(null, typeof(string)));
        }

        var value = Value(condition);
        var lowered = Expression.Constant(value.ToLowerInvariant(), typeof(string));
        var notNull = Expression.NotEqual(text, Expression.Constant(null, typeof(string)));
        var actual = Expression.Call(text, ToLowerMethod);

        // Negative operators are true for contacts that lack the field at all:
        // a contact with no plan is genuinely "not on the enterprise plan".
        return condition.Operator switch
        {
            SegmentOperator.Equals =>
                Expression.AndAlso(notNull, Expression.Equal(actual, lowered)),
            SegmentOperator.Contains =>
                Expression.AndAlso(notNull, Expression.Call(actual, ContainsMethod, lowered)),
            SegmentOperator.StartsWith =>
                Expression.AndAlso(notNull, Expression.Call(actual, StartsWithMethod, lowered)),
            SegmentOperator.EndsWith =>
                Expression.AndAlso(notNull, Expression.Call(actual, EndsWithMethod, lowered)),
            SegmentOperator.NotEquals =>
                Expression.OrElse(
                    Expression.Equal(text, Expression.Constant(null, typeof(string))),
                    Expression.NotEqual(actual, lowered)),
            SegmentOperator.NotContains =>
                Expression.OrElse(
                    Expression.Equal(text, Expression.Constant(null, typeof(string))),
                    Expression.Not(Expression.Call(actual, ContainsMethod, lowered))),
            _ => throw new SegmentRuleException(
                $"Operator '{condition.Operator}' cannot be used on text field '{field}'."),
        };
    }

    private static Expression BuildDate(
        string field, SegmentCondition condition, ParameterExpression parameter)
    {
        var isNullable = field == SegmentFields.LastSeenAt;
        var property = Expression.Property(
            parameter,
            field == SegmentFields.CreatedAt ? nameof(Contact.CreatedAt) : nameof(Contact.LastSeenAt));

        if (condition.Operator is SegmentOperator.Exists or SegmentOperator.NotExists)
        {
            if (!isNullable)
            {
                throw new SegmentRuleException($"Field '{field}' is always present.");
            }

            var nullConstant = Expression.Constant(null, typeof(DateTimeOffset?));
            return condition.Operator == SegmentOperator.Exists
                ? Expression.NotEqual(property, nullConstant)
                : Expression.Equal(property, nullConstant);
        }

        var raw = Value(condition);
        if (!DateTimeOffset.TryParse(
                raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var moment))
        {
            throw new SegmentRuleException(
                $"'{raw}' is not a date; field '{field}' needs an ISO-8601 value.");
        }

        // Compare through the non-nullable type on both sides so EF's value
        // converter applies to the constant as well as the column.
        Expression left = isNullable
            ? Expression.Property(property, nameof(Nullable<DateTimeOffset>.Value))
            : property;
        var right = Expression.Constant(moment, typeof(DateTimeOffset));

        Expression comparison = condition.Operator switch
        {
            SegmentOperator.Before => Expression.LessThan(left, right),
            SegmentOperator.After => Expression.GreaterThan(left, right),
            _ => throw new SegmentRuleException(
                $"Operator '{condition.Operator}' cannot be used on date field '{field}'."),
        };

        // A null date is neither before nor after anything.
        return isNullable
            ? Expression.AndAlso(
                Expression.NotEqual(property, Expression.Constant(null, typeof(DateTimeOffset?))),
                comparison)
            : comparison;
    }

    /// <summary>The string-valued expression for a built-in field or an attribute.</summary>
    private static Expression TextField(string field, ParameterExpression parameter)
    {
        if (SegmentFields.AttributeKey(field) is { } key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new SegmentRuleException("An attribute rule needs a key, e.g. 'attributes.plan'.");
            }

            // json_extract($.key) reads the attribute in SQL, so attribute
            // rules filter in the database like any other column.
            return Expression.Call(
                JsonExtractMethod,
                Expression.Property(parameter, nameof(Contact.AttributesJson)),
                Expression.Constant($"$.{key}"));
        }

        return field switch
        {
            SegmentFields.Name => Expression.Property(parameter, nameof(Contact.Name)),
            SegmentFields.Email => Expression.Property(parameter, nameof(Contact.Email)),
            SegmentFields.ExternalId => Expression.Property(parameter, nameof(Contact.ExternalId)),
            _ => throw new SegmentRuleException(
                $"Unknown field '{field}'. Use one of {string.Join(", ", SegmentFields.BuiltIn)} "
                + "or 'attributes.<key>'."),
        };
    }

    private static string Value(SegmentCondition condition) =>
        string.IsNullOrEmpty(condition.Value)
            ? throw new SegmentRuleException(
                $"Operator '{condition.Operator}' on '{condition.Field}' needs a value.")
            : condition.Value;
}
