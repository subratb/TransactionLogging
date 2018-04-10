using System;
using System.Data.Entity.Core.Common.CommandTrees;
using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;
using System.Data.Entity.Core.Metadata.Edm;
using System.Data.Entity.Infrastructure.Interception;
using System.Data.SqlClient;
using System.Linq;

namespace TransactionLogging
{
    //TODO
    public class TransactionCommandTreeInterceptor : IDbCommandTreeInterceptor
    {
        public void TreeCreated(DbCommandTreeInterceptionContext interceptionContext)
        {
            var logSchemaName = "test";
            var logTableName = "TransactionHistory";

            if (!(interceptionContext.OriginalResult.CommandTreeKind.Equals(DbCommandTreeKind.Insert) ||
                interceptionContext.OriginalResult.CommandTreeKind.Equals(DbCommandTreeKind.Update) ||
                interceptionContext.OriginalResult.CommandTreeKind.Equals(DbCommandTreeKind.Delete)))
            {
                return;
            }

            DbInsertCommandTree insertCommand = null;
            DbUpdateCommandTree updateCommand = null;
            DbDeleteCommandTree deleteCommand = null;

            if (interceptionContext.OriginalResult.CommandTreeKind.Equals(DbCommandTreeKind.Insert) && interceptionContext.OriginalResult.DataSpace == DataSpace.SSpace)
            {
                insertCommand = interceptionContext.Result as DbInsertCommandTree;
            }
            if (interceptionContext.OriginalResult.CommandTreeKind.Equals(DbCommandTreeKind.Update) && interceptionContext.OriginalResult.DataSpace == DataSpace.SSpace)
            {
                updateCommand = interceptionContext.Result as DbUpdateCommandTree;
            }
            if (interceptionContext.OriginalResult.CommandTreeKind.Equals(DbCommandTreeKind.Delete) && interceptionContext.OriginalResult.DataSpace == DataSpace.SSpace)
            {
                deleteCommand = interceptionContext.Result as DbDeleteCommandTree;
            }

            if (((insertCommand != null && insertCommand.Target.VariableName.Contains(logTableName)) ||
                (updateCommand != null && updateCommand.Target.VariableName.Contains(logTableName)) ||
                (deleteCommand != null && deleteCommand.Target.VariableName.Contains(logTableName))))
            {
                return;
            }

            var command = interceptionContext.DbContexts.First().Database.Connection.CreateCommand();
            command.CommandText = $"INSERT INTO [{logSchemaName}].[{logTableName}]([ChangedUser],[TransactionDetail],[ChangingUser],[TransactionDate]) SELECT @ChangedUser, @TransactionDetail_Created, @ChangingUser, @TransactionDate;" + command.CommandText;

            command.Parameters.AddRange(new[] {
                            new SqlParameter("@ChangedUser", ""),
                            new SqlParameter("@ChangingUser", ""),
                            new SqlParameter("@TransactionDetail_Updated", ""),
                            new SqlParameter("@TransactionDate", DateTime.Now)});


            if (insertCommand != null)
            {
                var context = interceptionContext.WithDbContext(interceptionContext.DbContexts.First());


            }


            if (interceptionContext.OriginalResult.DataSpace == DataSpace.SSpace)
            {
                var queryCommand = interceptionContext.Result as DbQueryCommandTree;
                if (queryCommand != null)
                {
                    var newQuery = queryCommand.Query.Accept(new StringTrimmerQueryVisitor());
                    interceptionContext.Result = new DbQueryCommandTree(
                        queryCommand.MetadataWorkspace,
                        queryCommand.DataSpace,
                        newQuery);
                }
            }

        }

        private class StringTrimmerQueryVisitor : DefaultExpressionVisitor
        {
            private static readonly string[] _typesToTrim = { "nvarchar", "varchar", "char", "nchar" };

            public override DbExpression Visit(DbNewInstanceExpression expression)
            {
                var arguments = expression.Arguments.Select(a =>
                {
                    var propertyArg = a as DbPropertyExpression;
                    if (propertyArg != null && _typesToTrim.Contains(propertyArg.Property.TypeUsage.EdmType.Name))
                    {
                        return EdmFunctions.Trim(a);
                    }

                    return a;
                });

                return DbExpressionBuilder.New(expression.ResultType, arguments);
            }
        }

    }
}
