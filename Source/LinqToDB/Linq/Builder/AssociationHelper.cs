﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using LinqToDB.SqlQuery;

namespace LinqToDB.Linq.Builder
{
	using LinqToDB.Expressions;
	using Extensions;
	using Mapping;
	using Reflection;

	static class AssociationHelper
	{
		// Returns
		// (ParentType p) => dc.GetTable<ObjectType>().Where(...)
		// (ParentType p) => dc.GetTable<ObjectType>().Where(...).DefaultIfEmpty
		public static LambdaExpression CreateAssociationQueryLambda(ExpressionBuilder builder, MemberInfo onMember, AssociationDescriptor association, 
			Type parentOriginalType,
			Type parentType, 
			Type objectType, bool inline, bool enforceDefault, 
			List<Tuple<MemberInfo, Expression?>[]>? loadWith, out bool isLeft)
		{
			var dataContextConstant = Expression.Constant(builder.DataContext, builder.DataContext.GetType());

			// We are trying to keep fast cache hit behaviour, so cache check should be added only if needed
			//
			bool shouldAddCacheCheck = false;

			bool cacheCheckAdded = false;

			LambdaExpression? definedQueryMethod  = null;
			if (association.HasQueryMethod())
			{
				// here we tell for Expression Comparer to compare optimized Association expressions
				//
				var closureMappingSchema = builder.MappingSchema;
				definedQueryMethod = (LambdaExpression)builder.AddQueryableMemberAccessors(onMember, mi =>
				{
					var queryLambda         = association.GetQueryMethod(parentType, objectType) ?? throw new InvalidOperationException();
					var optimizationContext = new ExpressionTreeOptimizationContext(closureMappingSchema);
					var optimizedExpr       = optimizationContext.ExposeExpression(queryLambda);
					    optimizedExpr       = optimizationContext.ExpandQueryableMethods(optimizedExpr);
					    optimizedExpr       = optimizedExpr.OptimizeExpression()!;
					return optimizedExpr;
				});

				cacheCheckAdded = true;

				var body = definedQueryMethod.GetBody(definedQueryMethod.Parameters[0], dataContextConstant);
				definedQueryMethod = Expression.Lambda(body, definedQueryMethod.Parameters[0]);
			}

			var shouldAddDefaultIfEmpty = enforceDefault;

			if (definedQueryMethod == null)
			{
				var parentParam = Expression.Parameter(parentType, "parent");
				var childParam  = Expression.Parameter(objectType, association.AliasName);

				var parentAccessor = TypeAccessor.GetAccessor(parentType);
				var childAccessor  = TypeAccessor.GetAccessor(objectType);

				Expression? predicate = null;
				for (var i = 0; i < association.ThisKey.Length; i++)
				{
					var parentName   = association.ThisKey[i];
					var parentMember = parentAccessor.Members.Find(m => m.MemberInfo.Name == parentName);

					if (parentMember == null)
						throw new LinqException("Association key '{0}' not found for type '{1}.", parentName,
							parentType);

					var childName = association.OtherKey[i];
					var childMember = childAccessor.Members.Find(m => m.MemberInfo.Name == childName);

					if (childMember == null)
						throw new LinqException("Association key '{0}' not found for type '{1}.", childName,
							objectType);

					var current = ExpressionBuilder.Equal(builder.MappingSchema,
						Expression.MakeMemberAccess(parentParam, parentMember.MemberInfo),
						Expression.MakeMemberAccess(childParam, childMember.MemberInfo));

					predicate = predicate == null ? current : Expression.AndAlso(predicate, current);
				}

				var expressionPredicate = association.GetPredicate(parentType, objectType);

				if (expressionPredicate != null)
				{
					shouldAddDefaultIfEmpty = true;
					shouldAddCacheCheck = true;

					var replacedBody = expressionPredicate.GetBody(parentParam, childParam);

					predicate = predicate == null ? replacedBody : Expression.AndAlso(predicate, replacedBody);
				}
				
				if (predicate == null)
					throw new LinqException("Can not generate Association predicate");

				if (inline && !shouldAddDefaultIfEmpty)
				{
					var ed = builder.MappingSchema.GetEntityDescriptor(objectType);
					if (ed.QueryFilterFunc != null)
					{
						shouldAddDefaultIfEmpty = true;
						shouldAddCacheCheck = true;
					}
				}

				var queryParam = Expression.Call(Methods.LinqToDB.GetTable.MakeGenericMethod(objectType), dataContextConstant);

				var filterLambda = Expression.Lambda(predicate, childParam);
				Expression body  = Expression.Call(Methods.Queryable.Where.MakeGenericMethod(objectType), queryParam,
					filterLambda);

				definedQueryMethod = Expression.Lambda(body, parentParam);
			}
			else
			{
				shouldAddDefaultIfEmpty = true;
				var bodyExpression = definedQueryMethod.Body.Unwrap();
				if (bodyExpression.NodeType == ExpressionType.Call)
				{
					var mc = (MethodCallExpression)bodyExpression;
					if (mc.IsSameGenericMethod(Methods.Queryable.DefaultIfEmpty, Methods.Queryable.DefaultIfEmptyValue))
						shouldAddDefaultIfEmpty = false;
				}
			}

			if (!cacheCheckAdded && shouldAddCacheCheck)
			{
				// here we tell for Expression Comparer to compare optimized Association expressions
				//
				var closureExpr          = definedQueryMethod;
				var closureMappingSchema = builder.MappingSchema;
				definedQueryMethod = (LambdaExpression)builder.AddQueryableMemberAccessors(onMember, mi =>
				{
					var optimizationContext = new ExpressionTreeOptimizationContext(closureMappingSchema);
					var optimizedExpr       = optimizationContext.ExposeExpression(queryLambda);
					    optimizedExpr       = optimizationContext.ExpandQueryableMethods(optimizedExpr);
					    optimizedExpr       = optimizedExpr.OptimizeExpression()!;
					return optimizedExpr;
				});
			}

			if (loadWith != null)
			{
				var associationLoadWith = GetLoadWith(loadWith)?
					.FirstOrDefault(li => li.MemberInfo == association.MemberInfo);

				if (associationLoadWith != null)
				{
					var loadWithFunc = associationLoadWith.Filter?.EvaluateExpression() as Delegate;

					if (loadWithFunc != null)
					{
						var body = definedQueryMethod.Body.Unwrap();
						var childTableType = typeof(ITable<>).MakeGenericType(objectType);
						body = body.Transform(e =>
						{
							if (e.NodeType == ExpressionType.Call)
							{
								var mc = (MethodCallExpression)e;
								if (mc.IsSameGenericMethod(Methods.LinqToDB.GetTable) && mc.Type == childTableType)
								{
									var filtered = (IQueryable)loadWithFunc.DynamicInvoke(mc.EvaluateExpression());
									e = filtered.Expression;
								}
							}

							return e;
						});

						definedQueryMethod = Expression.Lambda(body, definedQueryMethod.Parameters);
					}

					if (associationLoadWith.NextLoadWith != null)
					{
						definedQueryMethod = (LambdaExpression)EnrichTablesWithLoadWith(definedQueryMethod, objectType,
							associationLoadWith.NextLoadWith, builder.MappingSchema);
					}
					
				}
			}

			if (parentOriginalType != parentType)
			{
				// add discriminator filter
				var ed = builder.MappingSchema.GetEntityDescriptor(parentOriginalType);
				foreach (var inheritanceMapping in ed.InheritanceMapping)
				{
					if (inheritanceMapping.Type == parentType)
					{
						var objParam     = Expression.Parameter(objectType, "o");
						var filterLambda = Expression.Lambda(ExpressionBuilder.Equal(builder.MappingSchema,
							Expression.MakeMemberAccess(definedQueryMethod.Parameters[0], inheritanceMapping.Discriminator.MemberInfo),
							Expression.Constant(inheritanceMapping.Code)), objParam);
						
						var body = definedQueryMethod.Body.Unwrap();
						body = Expression.Call(Methods.Queryable.Where.MakeGenericMethod(objectType),
							body, filterLambda);
						definedQueryMethod = Expression.Lambda(body, definedQueryMethod.Parameters);

						shouldAddDefaultIfEmpty = true;
						break;
					}
				}
			}

			if (inline && shouldAddDefaultIfEmpty)
			{
				var body = definedQueryMethod.Body.Unwrap();
				body = Expression.Call(Methods.Queryable.DefaultIfEmpty.MakeGenericMethod(objectType), body);
				definedQueryMethod = Expression.Lambda(body, definedQueryMethod.Parameters);
				isLeft = true;
			}
			else
			{
				isLeft = false;
			}

			definedQueryMethod = (LambdaExpression)builder.ConvertExpressionTree(definedQueryMethod);
			definedQueryMethod = (LambdaExpression)builder.ConvertExpression(definedQueryMethod);
			definedQueryMethod = (LambdaExpression)definedQueryMethod.OptimizeExpression()!;

			return definedQueryMethod;
		}

		public static IBuildContext BuildAssociationInline(ExpressionBuilder builder, BuildInfo buildInfo, TableBuilder.TableContext tableContext, 
			MemberInfo onMember, AssociationDescriptor descriptor, bool inline, ref bool isOuter)
		{
			var elementType     = descriptor.GetElementType(builder.MappingSchema);
			var parentExactType = descriptor.GetParentElementType();
			
			var queryMethod = CreateAssociationQueryLambda(
				builder, onMember, descriptor, tableContext.OriginalType, parentExactType, elementType,
				inline, isOuter, tableContext.LoadWith, out isOuter);

			var parentRef   = new ContextRefExpression(queryMethod.Parameters[0].Type, tableContext);
			var body = queryMethod.GetBody(parentRef);

			var context = builder.BuildSequence(new BuildInfo(tableContext, body, new SelectQuery()));

			var tableSource = tableContext.SelectQuery.From.Tables.First();
			var join = new SqlFromClause.Join(isOuter ? JoinType.OuterApply : JoinType.CrossApply, context.SelectQuery,
				descriptor.GenerateAlias(), true, null);

			tableSource.Joins.Add(join.JoinedTable);
			
			return new AssociationContext(builder, descriptor, tableContext, context, join.JoinedTable);
		}

		public static IBuildContext BuildAssociationSelectMany(ExpressionBuilder builder, BuildInfo buildInfo, TableBuilder.TableContext tableContext, 
			MemberInfo onMember, AssociationDescriptor descriptor, ref bool isOuter)
		{
			var elementType = descriptor.GetElementType(builder.MappingSchema);

			var queryMethod = CreateAssociationQueryLambda(
				builder, onMember, descriptor, tableContext.OriginalType, tableContext.ObjectType, elementType,
				false, isOuter, tableContext.LoadWith, out isOuter);

			var parentRef   = new ContextRefExpression(queryMethod.Parameters[0].Type, tableContext);
			var body = queryMethod.GetBody(parentRef);

			IBuildContext context;

			context = builder.BuildSequence(new BuildInfo(buildInfo, body));
			context.SelectQuery.From.Tables[0].Alias = descriptor.GenerateAlias();

			return context;
		}

		public static Expression EnrichTablesWithLoadWith(Expression expression, Type entityType, List<Tuple<MemberInfo, Expression?>[]> loadWith, MappingSchema mappingSchema)
		{
			var tableType     = typeof(ITable<>).MakeGenericType(entityType);
			var newExpression = expression.Transform(e =>
			{
				if (e.NodeType == ExpressionType.Call)
				{
					var mc = (MethodCallExpression)e;
					if (mc.IsQueryable("GetTable") && tableType.IsSameOrParentOf(mc.Type))
					{
						e = EnrichLoadWith(mc, entityType, loadWith, mappingSchema);
					}
				}

				return e;
			});

			return newExpression;
		}

		public static Expression EnrichLoadWith(Expression table, Type entityType, List<Tuple<MemberInfo, Expression?>[]> loadWith, MappingSchema mappingSchema)
		{
			IQueryable queryable  = (IQueryable)table.EvaluateExpression()!;

			foreach (var members in loadWith)
			{
				var pLoadWith = Expression.Parameter(entityType, "t");
				var isPrevList = false;

				Expression obj = pLoadWith;

				foreach (var member in members)
				{
					if (isPrevList)
						obj = new GetItemExpression(obj, mappingSchema);

					if (member.Item1.DeclaringType != obj.Type)
						obj = Expression.Convert(obj, member.Item1.DeclaringType);
					obj = Expression.MakeMemberAccess(obj, member.Item1);

					isPrevList = typeof(IEnumerable).IsSameOrParentOf(obj.Type);
				}

				var queryFilter = members[members.Length - 1].Item2;

				if (queryFilter == null)
				{
					var method = Methods.LinqToDB.LoadWith.MakeGenericMethod(entityType, obj.Type);

					var lambda = Expression.Lambda(obj, pLoadWith);
					queryable  = (IQueryable)method.Invoke(null, new object[] { queryable, lambda });
				}
				else
				{
					var method =
						(isPrevList
							? Methods.LinqToDB.LoadWithQueryMany
							: Methods.LinqToDB.LoadWithQuerySingle).MakeGenericMethod(entityType,
							EagerLoading.GetEnumerableElementType(obj.Type, mappingSchema));

					if (isPrevList)
						obj = EagerLoading.EnsureEnumerable(obj, mappingSchema);

					var lambda = Expression.Lambda(obj, pLoadWith);

					queryable = (IQueryable)method.Invoke(null,
						new object[] { queryable, lambda, queryFilter.EvaluateExpression()! });
				}
			}

			return queryable.Expression;
		}

		public static Delegate? GetLoadWithFunc(List<Tuple<MemberInfo, Expression?>[]>? loadWith, MemberInfo memberInfo)
		{
			Delegate? loadWithFunc = null;
			if (loadWith != null)
			{
				loadWithFunc = GetLoadWith(loadWith)?
					.FirstOrDefault(li => li.MemberInfo == memberInfo)?.Filter?.EvaluateExpression() as Delegate;
			}

			return loadWithFunc;
		}

		public class LoadWithItem
		{
			public MemberInfo  MemberInfo   = null!;
			public Expression? Filter;
			public List<Tuple<MemberInfo, Expression?>[]> NextLoadWith = null!;
		}


		public static List<LoadWithItem> GetLoadWith(List<Tuple<MemberInfo, Expression?>[]> infos)
		{
			return
			(
				from lw in infos
				select new
				{
					head = lw.First(),
					tail = lw.Skip(1).ToArray()
				}
				into info
				group info by new { MemberInfo = info.head.Item1, Filter = info.head.Item2 } into gr
				select new LoadWithItem
				{
					MemberInfo   = gr.Key.MemberInfo,
					Filter       = gr.Key.Filter,
					NextLoadWith = (from i in gr where i.tail.Length > 0 select i.tail).ToList()
				}
			).ToList();
		}

	}

}
