﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Corax;
using Corax.Queries;
using Google.Protobuf.WellKnownTypes;
using JetBrains.Annotations;
using Nest;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Sparrow.Json;
using Sparrow.Server;
using Sparrow.Threading;
using Voron;
using BinaryExpression = Raven.Server.Documents.Queries.AST.BinaryExpression;

namespace Raven.Server.Documents.Indexes.Persistence.Corax
{
    public class CoraxQueryEvaluator : IDisposable
    {
        private readonly IndexSearcher _searcher;
        private readonly ByteStringContext _allocator;
        private IndexQueryServerSide _query;
        private const int TakeAll = -1;
        private const int ScoreId = -1;
        
        [CanBeNull]
        private FieldsToFetch _fieldsToFetch;

        public CoraxQueryEvaluator(IndexSearcher searcher)
        {
            _allocator = new(SharedMultipleUseFlag.None);
            _searcher = searcher;
        }

        public IQueryMatch Search(IndexQueryServerSide query, FieldsToFetch fieldsToFetch, int take = TakeAll)
        {
            _fieldsToFetch = fieldsToFetch;
            _query = query;

            var match = _query.Metadata.Query.Where is null 
                ? _searcher.AllEntries()
                : Evaluate(query.Metadata.Query.Where, false, take, default(NullScoreFunction));
            
            if (query.Metadata.OrderBy is not null)
                match = OrderBy(match, query.Metadata.Query.OrderBy, take);

            return match;
        }

        private IQueryMatch Evaluate<TScoreFunction>(QueryExpression condition, bool isNegated, int take, TScoreFunction scoreFunction)
            where TScoreFunction : IQueryScoreFunction
        {
            switch (condition)
            {
                case NegatedExpression negatedExpression:
                    isNegated = !isNegated;
                    return Evaluate(negatedExpression.Expression, isNegated, take, scoreFunction);
                case TrueExpression:
                case null:
                    return _searcher.AllEntries();
                case BetweenExpression betweenExpression:
                    return EvaluateBetween(betweenExpression, isNegated, take, scoreFunction);
                case MethodExpression methodExpression:
                    var expressionType = QueryMethod.GetMethodType(methodExpression.Name.Value);
                    string fieldName = string.Empty;
                    int fieldId;
                    switch (expressionType)
                    {
                        case MethodType.StartsWith:
                            fieldName = GetField(methodExpression.Arguments[0]);
                            fieldId = GetFieldIdInIndex(fieldName);
                            return _searcher.StartWithQuery(fieldName,
                                ((ValueExpression)methodExpression.Arguments[1]).Token.Value, scoreFunction, isNegated, fieldId);
                        case MethodType.EndsWith:
                            fieldName = GetField(methodExpression.Arguments[0]);
                            fieldId = GetFieldIdInIndex(fieldName);
                            return _searcher.EndsWithQuery(fieldName,
                                ((ValueExpression)methodExpression.Arguments[1]).Token.Value, scoreFunction, isNegated, fieldId);
                        case MethodType.Exact:
                            return BinaryEvaluator((BinaryExpression)methodExpression.Arguments[0], isNegated, take, scoreFunction);
                        case MethodType.Boost:
                            var boost = methodExpression.Arguments[1] as ValueExpression;
                            if (float.TryParse(boost?.Token.Value, out var constantValue) == false)
                                throw new InvalidQueryException("Invalid boost value.");

                            return Evaluate(methodExpression.Arguments[0], isNegated, TakeAll, new ConstantScoreFunction(constantValue));
                        case MethodType.Search:
                            return SearchMethod(methodExpression, isNegated, scoreFunction);
                        case MethodType.Exists:
                            fieldName = GetField(methodExpression.Arguments[0]);
                            return _searcher.ExistsQuery(fieldName);
                        default:
                            throw new NotImplementedException($"Method {nameof(expressionType)} is not implemented.");
                    }

                case InExpression inExpression:
                    return (inExpression.Source, inExpression.Values) switch
                    {
                        (FieldExpression f, List<QueryExpression> list) => EvaluateInExpression(f, list, scoreFunction),
                        _ => throw new NotSupportedException("InExpression.")
                    };

                case BinaryExpression be:
                    return BinaryEvaluator(be, isNegated, take, scoreFunction);
            }

            throw new EvaluateException($"Evaluation failed in {nameof(CoraxQueryEvaluator)}.");
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IQueryMatch SearchMethod<TScoreFunction>(MethodExpression expression, bool isNegated, TScoreFunction scoreFunction)
            where TScoreFunction : IQueryScoreFunction
        {
            var fieldName = $"search({GetField(expression.Arguments[0])})";
            var fieldId = GetFieldIdInIndex(fieldName);
            Constants.Search.Operator @operator = Constants.Search.Operator.Or;
            string searchTerm;


            if (expression.Arguments.Count is < 2 or > 3)
                throw new InvalidQueryException($"Invalid amount of parameter in {nameof(MethodType.Search)}.");

            if (expression.Arguments[1] is not ValueExpression searchParam)
                throw new InvalidQueryException($"You need to pass value in second argument of {nameof(MethodType.Search)}.");

            //STRUCTURE: <NAME><SEARCH_VALUE><OPERATOR>
            if (searchParam.Value is ValueTokenType.Parameter)
            {
                if (_query.QueryParameters.TryGet(searchParam.Token.Value, out searchTerm) == false)
                    throw new InvalidQueryException($"Cannot find {searchParam.Token.Value} in query.");
            }
            else
            {
                searchTerm = searchParam.Token.Value;
            }

            if (expression.Arguments.Count is not 3)
                return _searcher.SearchQuery(fieldName, searchTerm, @operator, isNegated, fieldId);


            if (expression.Arguments[2] is not FieldExpression operatorType)
                throw new InvalidQueryException($"Expected AND or OR in third argument of {nameof(MethodType.Search)}.");

            @operator = operatorType.FieldValue.ToLowerInvariant() switch
            {
                "and" => Constants.Search.Operator.And,
                "or" => Constants.Search.Operator.Or,
                _ => throw new InvalidQueryException($"Expected AND or OR in third argument of {nameof(MethodType.Search)}.")
            };


            return _searcher.SearchQuery<TScoreFunction>(fieldName, searchTerm, scoreFunction, @operator, fieldId, isNegated);
        }

        [SkipLocalsInit]
        private IQueryMatch BinaryEvaluator<TScoreFunction>(BinaryExpression expression, bool isNegated, int take, TScoreFunction scoreFunction)
            where TScoreFunction : IQueryScoreFunction
        {
            if (isNegated == true)
                expression.Operator = GetNegated(expression.Operator);

            ValueExpression value;
            FieldExpression field;
            string fieldName;
            switch (expression.Operator)
            {
                case OperatorType.Or:
                    return _searcher.Or(Evaluate(expression.Left, isNegated, take, scoreFunction), Evaluate(expression.Right, isNegated, take, scoreFunction));
                case OperatorType.Equal:
                {
                    value = (ValueExpression)expression.Right;
                    field = (FieldExpression)expression.Left;
                    fieldName = GetField(field);
                    var match = _searcher.TermQuery(fieldName, value.GetValue(_query.QueryParameters).ToString(), GetFieldIdInIndex(fieldName));
                    return scoreFunction is NullScoreFunction
                        ? match
                        : _searcher.Boost(match, scoreFunction);
                }
                case OperatorType.NotEqual:
                    value = (ValueExpression)expression.Right;
                    field = (FieldExpression)expression.Left;
                    fieldName = GetField(field);
                    return isNegated
                        ? _searcher.TermQuery(fieldName, value.GetValue(_query.QueryParameters).ToString(), GetFieldIdInIndex(fieldName))
                        : _searcher.NotEquals(_searcher.AllEntries(), GetFieldIdInIndex(fieldName), value.GetValue(_query.QueryParameters).ToString());
            }

            if (expression.IsRangeOperation)
                return EvaluateUnary(expression.Operator, _searcher.AllEntries(), expression, isNegated, take, scoreFunction);

            var left = expression.Left as BinaryExpression;
            var isLeftUnary = IsUnary(left);
            var right = expression.Right as BinaryExpression;
            var isRightUnary = IsUnary(right);

            return (isLeftUnary, isRightUnary) switch
            {
                (false, false) => _searcher.And(Evaluate(expression.Left, isNegated, take, scoreFunction), Evaluate(expression.Right, isNegated, take, scoreFunction)),
                (true, true) => EvaluateUnary(right.Operator, EvaluateUnary(left.Operator, _searcher.AllEntries(), left, isNegated, take, scoreFunction), right,
                    isNegated, take, scoreFunction),
                (true, false) => EvaluateUnary(left.Operator, Evaluate(expression.Right, isNegated, take, scoreFunction), left, isNegated, take, scoreFunction),
                (false, true) => EvaluateUnary(right.Operator, Evaluate(expression.Left, isNegated, take, scoreFunction), right, isNegated, take, scoreFunction),
            };
        }

        private IQueryMatch AllEntries<TScoreFunction>(TScoreFunction scoreFunction)
            where TScoreFunction : IQueryScoreFunction
        {
            return _searcher.Boost(_searcher.AllEntries(), scoreFunction);
        }

        [SkipLocalsInit]
        private IQueryMatch EvaluateUnary<TScoreFunction>(OperatorType type, in IQueryMatch previousMatch, BinaryExpression value, bool isNegated, int take,
            TScoreFunction scoreFunction)
            where TScoreFunction : IQueryScoreFunction
        {
            var fieldId = GetFieldIdInIndex(GetField((FieldExpression)value.Left));
            var field = GetValue((ValueExpression)value.Right);
            var fieldValue = field.FieldValue;
            Slice sliceValue = default;
            if (fieldValue is ValueTokenType.String)
                Slice.From(_allocator, fieldValue.ToString(), out sliceValue);

            var match = (type, field.ValueType) switch
            {
                (OperatorType.LessThan, ValueTokenType.Double) => _searcher.LessThan(previousMatch, fieldId, (double)fieldValue, take),
                (OperatorType.LessThan, ValueTokenType.Long) => _searcher.LessThan(previousMatch, fieldId, (long)fieldValue, take),
                (OperatorType.LessThan, ValueTokenType.String) => _searcher.LessThan(previousMatch, fieldId, sliceValue, take),

                (OperatorType.LessThanEqual, ValueTokenType.Double) => _searcher.LessThanOrEqual(previousMatch, fieldId, (double)fieldValue, take),
                (OperatorType.LessThanEqual, ValueTokenType.Long) => _searcher.LessThanOrEqual(previousMatch, fieldId, (long)fieldValue, take),
                (OperatorType.LessThanEqual, ValueTokenType.String) => _searcher.LessThanOrEqual(previousMatch, fieldId, sliceValue, take),

                (OperatorType.GreaterThan, ValueTokenType.Double) => _searcher.GreaterThan(previousMatch, fieldId, (double)fieldValue, take),
                (OperatorType.GreaterThan, ValueTokenType.Long) => _searcher.GreaterThan(previousMatch, fieldId, (long)fieldValue, take),
                (OperatorType.GreaterThan, ValueTokenType.String) => _searcher.GreaterThan(previousMatch, fieldId, sliceValue, take),

                (OperatorType.GreaterThanEqual, ValueTokenType.Double) => _searcher.GreaterThanOrEqual(previousMatch, fieldId, (double)fieldValue, take),
                (OperatorType.GreaterThanEqual, ValueTokenType.Long) => _searcher.GreaterThanOrEqual(previousMatch, fieldId, (long)fieldValue, take),
                (OperatorType.GreaterThanEqual, ValueTokenType.String) => _searcher.GreaterThanOrEqual(previousMatch, fieldId, sliceValue, take),

                _ => throw new EvaluateException($"Got {type} and the value: {field.ValueType} at UnaryMatch.")
            };

            return scoreFunction is NullScoreFunction
                ? match
                : _searcher.Boost(match, scoreFunction);
        }

        private IQueryMatch EvaluateBetween<TScoreFunction>(BetweenExpression betweenExpression, bool negated, int take, TScoreFunction scoreFunction)
            where TScoreFunction : IQueryScoreFunction
        {
            var exprMin = GetValue(betweenExpression.Min);
            var exprMax = GetValue(betweenExpression.Max);
            var fieldId = GetFieldIdInIndex(GetField((FieldExpression)betweenExpression.Source));

            IQueryMatch match = (exprMin.ValueType, exprMax.ValueType, negated) switch
            {
                (ValueTokenType.Long, ValueTokenType.Long, false) => _searcher.Between(_searcher.AllEntries(), fieldId, (long)exprMin.FieldValue,
                    (long)exprMax.FieldValue, take),
                (ValueTokenType.Long, ValueTokenType.Long, true) => _searcher.NotBetween(_searcher.AllEntries(), fieldId, (long)exprMin.FieldValue,
                    (long)exprMax.FieldValue, take),

                (ValueTokenType.Double, ValueTokenType.Double, false) => _searcher.Between(_searcher.AllEntries(), fieldId, (double)exprMin.FieldValue,
                    (double)exprMax.FieldValue, take),
                (ValueTokenType.Double, ValueTokenType.Double, true) => _searcher.NotBetween(_searcher.AllEntries(), fieldId, (double)exprMin.FieldValue,
                    (double)exprMax.FieldValue, take),

                (ValueTokenType.String, ValueTokenType.String, false) => _searcher.Between(_searcher.AllEntries(), fieldId, (long)exprMin.FieldValue,
                    (long)exprMax.FieldValue, take),
                (ValueTokenType.String, ValueTokenType.String, true) => _searcher.NotBetween(_searcher.AllEntries(), fieldId, (long)exprMin.FieldValue,
                    (long)exprMax.FieldValue, take),

                _ => throw new EvaluateException(
                    $"Got {(exprMin.ValueType is ValueTokenType.Double or ValueTokenType.Long or ValueTokenType.String ? exprMax.ValueType : exprMin.ValueType)} but expected: {ValueTokenType.String}, {ValueTokenType.Long}, {ValueTokenType.Double}.")
            };

            return scoreFunction is NullScoreFunction
                ? match
                : _searcher.Boost(match, scoreFunction);
        }

        private IQueryMatch EvaluateInExpression<TScoreFunction>(FieldExpression f, List<QueryExpression> list, TScoreFunction scoreFunction)
            where TScoreFunction : IQueryScoreFunction
        {
            var values = new List<string>();
            foreach (ValueExpression v in list)
                values.Add(v.Token.Value);

            return scoreFunction is NullScoreFunction
                ? _searcher.InQuery(f.FieldValue, values)
                : _searcher.InQuery(f.FieldValue, values, scoreFunction);
        }

        private (ValueTokenType ValueType, object FieldValue) GetValue(ValueExpression expr)
        {
            var valueType = expr.Value;
            object fieldValue = GetFieldValue(expr);
            if (valueType != ValueTokenType.Parameter)
                return (valueType, fieldValue);


            switch (fieldValue)
            {
                case LazyNumberValue fV:
                    valueType = ValueTokenType.Double;
                    fieldValue = fV.ToDouble(CultureInfo.InvariantCulture);
                    break;
                case long:
                    valueType = ValueTokenType.Long;
                    break;
                case decimal value:
                    valueType = ValueTokenType.Double;
                    fieldValue = Convert.ToDouble(value);
                    break;
                case double:
                    valueType = ValueTokenType.Double;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported type: ${fieldValue}.");
            }

            return (valueType, fieldValue);
        }

        private static OperatorType GetNegated(OperatorType current) =>
            current switch
            {
                OperatorType.LessThan => OperatorType.GreaterThanEqual,
                OperatorType.LessThanEqual => OperatorType.GreaterThan,
                OperatorType.GreaterThan => OperatorType.LessThanEqual,
                OperatorType.GreaterThanEqual => OperatorType.LessThan,
                OperatorType.Equal => OperatorType.NotEqual,
                OperatorType.NotEqual => OperatorType.Equal,
                OperatorType.And => OperatorType.Or,
                OperatorType.Or => OperatorType.And,
                _ => throw new ArgumentOutOfRangeException(nameof(current), current, null)
            };

        private static MatchCompareFieldType OrderTypeFieldConverter(OrderByFieldType original) =>
            original switch
            {
                OrderByFieldType.Double => MatchCompareFieldType.Floating,
                OrderByFieldType.Long => MatchCompareFieldType.Integer,
                OrderByFieldType.AlphaNumeric => MatchCompareFieldType.Integer,
                _ => MatchCompareFieldType.Sequence
            };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetFieldIdInIndex(string fieldName, bool isFieldType = true)
        {
            if (_fieldsToFetch is null)
                throw new InvalidQueryException("Field doesn't found in Index.");

            if (isFieldType && _fieldsToFetch.IndexFields.TryGetValue(fieldName, out var indexField))
            {
                return indexField.Id;
            }

            return fieldName switch
            {
                Client.Constants.Documents.Indexing.Fields.DocumentIdFieldName => 0,
                "score" => ScoreId,
                _ => throw new InvalidDataException($"Field {fieldName} does not found in current index.")
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private object GetFieldValue(ValueExpression f) => f.GetValue(_query.QueryParameters);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsUnary(BinaryExpression binaryExpression) => binaryExpression is not null && binaryExpression.IsRangeOperation;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string GetField(QueryExpression queryExpression) => queryExpression switch
        {
            FieldExpression fieldExpression => _query.Metadata.GetIndexFieldName(fieldExpression, _query.QueryParameters).Value,
            ValueExpression valueExpression => valueExpression.Token.Value,
            MethodExpression methodExpression => methodExpression.Name.Value switch
            { 
                Client.Constants.Documents.Indexing.Fields.DocumentIdMethodName => Raven.Client.Constants.Documents.Indexing.Fields.DocumentIdFieldName,
                _ => methodExpression.Name.Value
            },
            _ => throw new InvalidDataException("Unknown type for now.")
            
        };

        private enum ComparerType
        {
            Ascending,
            Descending,
            Boosting
        }

        private ComparerType GetComparerType(bool ascending, int fieldId) => (ascending, fieldId) switch
        {
            (_, ScoreId) => ComparerType.Boosting,
            (true, _) => ComparerType.Ascending,
            (false, _) => ComparerType.Descending
        };

        private IQueryMatch OrderBy(IQueryMatch match, List<(QueryExpression Expression, OrderByFieldType FieldType, bool Ascending)> orders, int take)
        {
            switch (orders.Count)
            {
                //Note: we want to use generics up to 3 comparers. This way we gonna avoid virtual calls in most cases.
                case 1:
                {
                    var fe = orders[0].Expression as FieldExpression;
                    var me = orders[0].Expression as MethodExpression;
                    if (fe is null && me is null)
                        throw new InvalidQueryException($"The expression used in the ORDER BY clause is wrong.");

                    var id = fe is null ? GetFieldIdInIndex(me.Name.Value, false) : GetFieldIdInIndex(GetField(fe));
                    var orderTypeField = OrderTypeFieldConverter(orders[0].FieldType);

                    match = id switch
                    {
                        ScoreId => _searcher.OrderByScore(match, take),
                        >= 0 => orders[0].Ascending
                            ? _searcher.OrderByAscending(match, id, orderTypeField, take)
                            : _searcher.OrderByDescending(match, id, orderTypeField, take),
                        _ => throw new InvalidDataException("Unknown fieldId in ORDER BY clause.")
                    };


                    return match;
                }
                case 2:
                {
                    var firstOrder = orders[0];
                    var secondOrder = orders[1];
                    var firstExpression = firstOrder.Expression as FieldExpression;
                    var secondExpression = secondOrder.Expression as FieldExpression;
                    var firstMethod = firstOrder.Expression as MethodExpression;
                    var secondMethod = secondOrder.Expression as MethodExpression;
                    
                    if (firstExpression is null && firstMethod is null || secondExpression is null && secondMethod is null)
                    {
                        throw new InvalidQueryException($"The expression used in the ORDER BY clause is wrong.");
                    }

                    var firstId = firstExpression is null ? GetFieldIdInIndex(firstMethod.Name.Value, false) : GetFieldIdInIndex(GetField(firstExpression));
                    var firstTypeField = OrderTypeFieldConverter(firstOrder.FieldType);
                    var secondId = secondExpression is null ? GetFieldIdInIndex(secondMethod.Name.Value, false) : GetFieldIdInIndex(GetField(secondExpression));
                    var secondTypeField = OrderTypeFieldConverter(secondOrder.FieldType);

                    return (GetComparerType(firstOrder.Ascending, firstId), GetComparerType(secondOrder.Ascending, secondId)) switch
                    {
                        (ComparerType.Ascending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                            new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                            new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField)),

                        (ComparerType.Descending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                            new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                            new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField)),

                        (ComparerType.Ascending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                            new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                            new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField)),

                        (ComparerType.Descending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                            new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                            new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField)),

                        (ComparerType.Ascending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                            new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                            default(BoostingComparer)),

                        (ComparerType.Boosting, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                            default(BoostingComparer),
                            new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField)),

                        (ComparerType.Descending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                            new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                            default(BoostingComparer)),

                        (ComparerType.Boosting, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                            default(BoostingComparer),
                            new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField)),

                        (ComparerType.Boosting, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                            default(BoostingComparer),
                            default(BoostingComparer)),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                }
                case 3:
                {
                    var firstOrder = orders[0];
                    var secondOrder = orders[1];
                    var thirdOrder = orders[2];
                    var firstExpression = firstOrder.Expression as FieldExpression;
                    var secondExpression = secondOrder.Expression as FieldExpression;
                    var thirdExpression = thirdOrder.Expression as FieldExpression;
                    var firstMethod = firstOrder.Expression as MethodExpression;
                    var secondMethod = secondOrder.Expression as MethodExpression;
                    var thirdMethod = thirdOrder.Expression as MethodExpression;


                    if (firstExpression is null && firstMethod is null || secondExpression is null && secondMethod is null || thirdExpression is null && thirdMethod is null)
                    {
                        throw new InvalidQueryException("The expression used in the ORDER BY clause is wrong.");
                    }

                    var firstId = firstExpression is null ? GetFieldIdInIndex(firstMethod.Name.Value, false) : GetFieldIdInIndex(GetField(firstExpression));
                    var firstTypeField = OrderTypeFieldConverter(firstOrder.FieldType);
                    var secondId = secondExpression is null ? GetFieldIdInIndex(secondMethod.Name.Value, false) : GetFieldIdInIndex(GetField(secondExpression));
                    var secondTypeField = OrderTypeFieldConverter(secondOrder.FieldType);
                    var thirdId = thirdExpression is null ? GetFieldIdInIndex(thirdMethod.Name.Value, false) : GetFieldIdInIndex(GetField(thirdExpression));
                    var thirdTypeField = OrderTypeFieldConverter(thirdOrder.FieldType);


                    return (GetComparerType(firstOrder.Ascending, firstId), GetComparerType(secondOrder.Ascending, secondId),
                            GetComparerType(thirdOrder.Ascending, thirdId)) switch
                        {
                            (ComparerType.Ascending, ComparerType.Ascending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Ascending, ComparerType.Ascending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Ascending, ComparerType.Ascending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                default(BoostingComparer)),

                            (ComparerType.Ascending, ComparerType.Descending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Ascending, ComparerType.Descending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Ascending, ComparerType.Descending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                default(BoostingComparer)),

                            (ComparerType.Ascending, ComparerType.Boosting, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                default(BoostingComparer),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Ascending, ComparerType.Boosting, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                default(BoostingComparer),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Ascending, ComparerType.Boosting, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.AscendingMatchComparer(_searcher, firstId, firstTypeField),
                                default(BoostingComparer),
                                default(BoostingComparer)),

                            (ComparerType.Descending, ComparerType.Ascending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Descending, ComparerType.Ascending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Descending, ComparerType.Ascending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                default(BoostingComparer)),

                            (ComparerType.Descending, ComparerType.Descending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Descending, ComparerType.Descending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Descending, ComparerType.Descending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                default(BoostingComparer)),

                            (ComparerType.Descending, ComparerType.Boosting, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                default(BoostingComparer),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Descending, ComparerType.Boosting, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                default(BoostingComparer),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Descending, ComparerType.Boosting, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                new SortingMatch.DescendingMatchComparer(_searcher, firstId, firstTypeField),
                                default(BoostingComparer),
                                default(BoostingComparer)),

                            (ComparerType.Boosting, ComparerType.Ascending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Boosting, ComparerType.Ascending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Boosting, ComparerType.Ascending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                new SortingMatch.AscendingMatchComparer(_searcher, secondId, secondTypeField),
                                default(BoostingComparer)),

                            (ComparerType.Boosting, ComparerType.Descending, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Boosting, ComparerType.Descending, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Boosting, ComparerType.Descending, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                new SortingMatch.DescendingMatchComparer(_searcher, secondId, secondTypeField),
                                default(BoostingComparer)),

                            (ComparerType.Boosting, ComparerType.Boosting, ComparerType.Ascending) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                default(BoostingComparer),
                                new SortingMatch.AscendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Boosting, ComparerType.Boosting, ComparerType.Descending) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                default(BoostingComparer),
                                new SortingMatch.DescendingMatchComparer(_searcher, thirdId, thirdTypeField)),

                            (ComparerType.Boosting, ComparerType.Boosting, ComparerType.Boosting) => SortingMultiMatch.Create(_searcher, match,
                                default(BoostingComparer),
                                default(BoostingComparer),
                                default(BoostingComparer)),
                        };
                }
            }

            var comparers = new IMatchComparer[orders.Count];
            for (int i = 0; i < orders.Count; ++i)
            {
                if (orders[i].Expression is not FieldExpression fe)
                {
                    if (orders[i].Expression is MethodExpression me)
                    {
                        comparers[i] = me.Name.Value switch
                        {
                            "id" => orders[i].Ascending
                                ? new SortingMatch.AscendingMatchComparer(_searcher, 0, OrderTypeFieldConverter(orders[i].FieldType))
                                : new SortingMatch.DescendingMatchComparer(_searcher, 0, OrderTypeFieldConverter(orders[i].FieldType)),
                            "score" => default(BoostingComparer),
                            _ => throw new InvalidDataException($"Unknown {nameof(MethodExpression)} as argument of ORDER BY. Have you mean id() or score()?")
                        };
                        continue;
                    }

                    comparers[i] = default(SortingMultiMatch.NullComparer);
                    continue;
                }


                var id = GetFieldIdInIndex(GetField(fe));
                var orderTypeField = OrderTypeFieldConverter(orders[i].FieldType);
                comparers[i] = orders[i].Ascending
                    ? new SortingMatch.AscendingMatchComparer(_searcher, id, orderTypeField)
                    : new SortingMatch.DescendingMatchComparer(_searcher, id, orderTypeField);
            }

            return orders.Count switch
            {
                2 => SortingMultiMatch.Create(_searcher, match, comparers[0], comparers[1]),
                3 => SortingMultiMatch.Create(_searcher, match, comparers[0], comparers[1], comparers[2]),
                4 => SortingMultiMatch.Create(_searcher, match, comparers[0], comparers[1], comparers[2], comparers[3]),
                5 => SortingMultiMatch.Create(_searcher, match, comparers[0], comparers[1], comparers[2], comparers[3], comparers[4]),
                6 => SortingMultiMatch.Create(_searcher, match, comparers[0], comparers[1], comparers[2], comparers[3], comparers[4], comparers[5]),
                7 => SortingMultiMatch.Create(_searcher, match, comparers[0], comparers[1], comparers[2], comparers[3], comparers[4], comparers[5], comparers[6]),
                8 => SortingMultiMatch.Create(_searcher, match, comparers[0], comparers[1], comparers[2], comparers[3], comparers[4], comparers[5], comparers[6],
                    comparers[7]),
                _ => throw new InvalidQueryException("Maximum amount of comparers in ORDER BY clause is 8.")
            };
        }

        public void Dispose()
        {
            _allocator?.Dispose();
        }
    }
}