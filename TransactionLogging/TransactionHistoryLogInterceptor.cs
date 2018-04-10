using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity;
using System.Data.Entity.Core;
using System.Data.Entity.Core.Metadata.Edm;
using System.Data.Entity.Core.Objects;
using System.Data.Entity.Core.Objects.DataClasses;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Infrastructure.Interception;
using System.Data.Entity.Utilities;
using System.Data.SqlClient;
using System.Linq;

namespace TransactionLogging
{
    /// This is a entity framework interceptor to log transaction history.
    /// include below section in your web.config/app.config to enable feature of this interceptor
    /// "TransactionHistory" - replace this value with your own transaction history table's name.
    /// <interceptor type="TransactionHistoryLogInterceptor,TransactionLogging">
    ///   <parameters>
    ///     <parameter value = "TransactionHistory" />
    ///   </ parameters >
    /// </ interceptor >
    public class TransactionHistoryLogInterceptor : DbCommandInterceptor, IDisposable
    {
        //TODO:Schema wise transaction logging

        private string logTableSchema = "test";

        private string logTableName = "TransactionHistory";

        private DbContext commandInterceptionContext = null;

        private bool logged = false;

        public string[] IgnoredProperties
        {
            get
            {
                return new string[] { "AddUser", "ModUser", "AddDate", "ModDate" };
            }
        }

        private bool IsLogTableNameSpecified { get { return string.IsNullOrWhiteSpace(this.logTableName); } }

        private string TransactionHistoryLogCommandString
        {
            get
            {
                return $"INSERT INTO [{logTableSchema}].[{logTableName}]([TransactionDetail],[ChangingUser],[TransactionDate]) SELECT @TransactionDetail,@ChangingUser,@TransactionDate;";
            }
        }

        /// <summary>
        /// Creates a new logger that will send log changes to transaction history log.
        /// </summary>
        public TransactionHistoryLogInterceptor()
        {
        }

        /// <summary>
        /// Creates a new logger that will log changes to transaction history log.
        /// </summary>
        /// <param name="transactionHistoryLogTableName">The name of the transaction history log table.</param>
        public TransactionHistoryLogInterceptor(string transactionHistoryLogTableName)
        {
            this.logTableName = transactionHistoryLogTableName;
        }


        private bool IsDML(DbCommand command)
        {
            return
                command.CommandText.StartsWith("UPDATE") ||
                command.CommandText.StartsWith("INSERT") ||
                command.CommandText.StartsWith("DELETE");
        }

        private bool IsTransactionHistoryLogCommand(DbCommand command)
        {
            return command.CommandText.Contains(this.logTableName);
        }

        private SqlParameter[] CreateLogParameters(string detail, string changingUser)
        {
            return new[] {
                new SqlParameter("@TransactionDetail", detail),
                new SqlParameter("@ChangingUser", changingUser),
                new SqlParameter("@TransactionDate", DateTime.Now)
            };
        }
        private string GetTransactionLogDetail<TResult>(DbCommandInterceptionContext<TResult> interceptionContext)
        {
            //List all changed properties for each entity
            //string  - entity set, string - field name, object - old value, object - new value
            List<Tuple<string, List<Tuple<string, object, object>>>> transactionItems = new List<Tuple<string, List<Tuple<string, object, object>>>>();

            interceptionContext.DbContexts.ToList().ForEach(o => {

                //SELECT ADDED, DELETED AND MODIFIED ENTITIES
                var added = (o as IObjectContextAdapter).ObjectContext.ObjectStateManager.GetObjectStateEntries(EntityState.Added);
                var deleted = (o as IObjectContextAdapter).ObjectContext.ObjectStateManager.GetObjectStateEntries(EntityState.Deleted);
                var changed = (o as IObjectContextAdapter).ObjectContext.ObjectStateManager.GetObjectStateEntries(EntityState.Modified);

                //ADDED
                //GET ALL PROPERTIES OF ADDED ENTITIES
                added.ToList().ForEach(oa =>
                {
                    var mProperties = oa.CurrentValues.DataRecordInfo.FieldMetadata.Select(field => field.FieldType.Name);

                    //GET FOREIGN ENTITIES WITH THEIR LEBEL
                    transactionItems.Add(new Tuple<string, List<Tuple<string, object, object>>>(oa.EntitySet.Name, mProperties.Select(prop => new Tuple<string, object, object>(prop, null, oa.CurrentValues[prop])).Union(this.GetForeignKeyLabels((o as IObjectContextAdapter).ObjectContext, oa)).ToList()));
                });

                //DELETED
                deleted.ToList().ForEach(od =>
                {
                    var mProperties = od.GetUpdatableOriginalValues().DataRecordInfo.FieldMetadata.Select(prop => prop.FieldType.Name);
                    transactionItems.Add(new Tuple<string, List<Tuple<string, object, object>>>(od.EntitySet.Name, mProperties.Select(prop => new Tuple<string, object, object>(prop, od.OriginalValues[prop], null)).Union(this.GetForeignKeyLabels((o as IObjectContextAdapter).ObjectContext, od)).ToList()));
                });

                //MODIFIED
                foreach (var oc in changed)
                {
                    var entry = o.Entry(oc.Entity);
                    entry.OriginalValues.SetValues(entry.GetDatabaseValues());

                    //GET MODIFIED PROPERTIES
                    var mProperties = oc.GetModifiedProperties().Where(prop => oc.IsPropertyChanged(prop));
                    //IF MODIFIED PROPERTIES ARE ALL IGNORED PROPERTIES,SKIP THE ENTITY
                    if (mProperties.Intersect(this.IgnoredProperties).Count().Equals(mProperties.Count()))
                    {
                        continue;
                    }

                    //ALSO, FOR MODIFIED ENTITY, ADD THE ENTITY KEY, SO THAT WE KNOW WHICH ENTITY WAS UPDATED
                    var entityKeyProperties = oc.EntityKey.EntityKeyValues.Select(prop => prop.Key);

                    //FIND LABEL PROPERTIES
                    //LEBEL PROPERTIES HELP US IN PUTTING NAMES WITH ID VALUES IN A TRANSACTION HISTORY RECORD
                    //SO THAT, IT MAKES SENSE(IDs ARE NOT HELPFUL, NAMES ARE) FOR END USER
                    var entityLabelProperties = oc.Entity.GetType().GetProperties().Where(y => y.GetCustomAttributes(true).Any(z => z is EntityLabelAttribute)).Select(P => P.Name);

                    var allChangedProperties = mProperties.Union(entityKeyProperties).Union(entityLabelProperties);

                    var transactionItem = allChangedProperties.Select(prop => new Tuple<string, object, object>(prop, oc.OriginalValues[prop], oc.CurrentValues[prop])).ToList();

                    //GET FOREIGN ENTITIES WITH THEIR LEBEL
                    transactionItem.AddRange(this.GetForeignKeyLabels((o as IObjectContextAdapter).ObjectContext, oc));

                    transactionItems.Add(new Tuple<string, List<Tuple<string, object, object>>>(oc.EntitySet.Name, transactionItem.ToList()));
                }

            });


            //remove duplicates, remove unnecessary fields
            var distinctItemsWithRequiredFields = new List<Tuple<string, object, object>>();


            //create a list of all properties, separated by index, table name, field name
            //(string) - index + table name, (string) - field name, (object) - old value, (object)  - new value
            List<Tuple<string, string, object, object>> transactionProperties = new List<Tuple<string, string, object, object>>();

            //remember, Item1 is EntitySet Name
            //group by entity to create a indexed list of entities from the same table
            var groupByEntity = transactionItems.GroupBy(i => i.Item1);
            groupByEntity.ToList().ForEach(entitySet =>
            {
                var i = 0;
                foreach (var transactionItem in entitySet)
                {
                    transactionItem.Item2.ForEach(item =>
                    {
                        //check if the property is for one of the ignored properties
                        //check if this entry is already present
                        if (!this.IgnoredProperties.Contains(item.Item1) && !distinctItemsWithRequiredFields.Contains(item))
                        {
                            distinctItemsWithRequiredFields.Add(item);
                            transactionProperties.Add(new Tuple<string, string, object, object>(i + "." + entitySet.Key, item.Item1, item.Item2, item.Item3));
                        }
                    });
                    i++;
                }
            });

            return transactionProperties.ToJsonString();
        }

        private List<Tuple<string, object, object>> GetForeignKeyLabels(ObjectContext context, ObjectStateEntry oce)
        {
            List<Tuple<string, object, object>> items = new List<Tuple<string, object, object>>();

            //FIND FOREIGN KEYS,GET THOSE FOREIGN KEY ENTITY, GET THEIR ENTITY LABELS
            var navigations = ((EntityType)oce.EntitySet.ElementType).DeclaredNavigationProperties;

            //INCLUDE THESE ENTITY LABELS IN MODIFIED PROPERTIES, SO THAT IT GETS LOGGED
            foreach (var navigation in navigations)
            {
                var rel = oce.RelationshipManager.GetRelatedEnd(navigation.RelationshipType.FullName, navigation.ToEndMember.Name);

                //&& rel.IsLoaded
                if (rel != null && rel is EntityReference)
                {
                    var reference = (rel as EntityReference);
                    //for added, deleted entities, we need to retrieve all the foreign objects
                    //but for changed entities, we only need to retrieve the foreign objects 
                    //if the foreign key it self has changed on the object
                    if (oce.State == EntityState.Modified)
                    {
                        //check if any of the modified property is part of this foreign key
                        if (reference.EntityKey.EntityKeyValues.Select(kv => kv.Key).Intersect(oce.GetModifiedProperties().Where(prop => oce.IsPropertyChanged(prop))).Count() <= 0)
                        {
                            continue;
                        }
                    }

                    object entity;
                    //if the object is being deleted, retrieve with a special call to TryGetObjectByKey
                    if (oce.State == EntityState.Deleted)
                    {
                        context.TryGetObjectByKey(oce, rel, out entity);
                    }
                    else
                    {
                        rel.Load();
                        entity = reference.GetType().GetProperty("Value").GetValue(reference);
                    }

                    object oldEntity;
                    //IF THE OBJECT IS ADDED OR DETACHED THERE IS NO ORIGINAL VALUES
                    var gotOldEntity = !(oce.State == EntityState.Added || oce.State == EntityState.Detached || oce.State == EntityState.Deleted);
                    if (gotOldEntity)
                    {
                        //CHECK IF WE CAN GET BACK ORIGINAL VALUES
                        var originalKeys = reference.EntityKey.EntityKeyValues.Select(kv => new EntityKeyMember(kv.Key, oce.OriginalValues[kv.Key]));
                        gotOldEntity = context.TryGetObjectByKey(new System.Data.Entity.Core.EntityKey(reference.EntityKey.EntityContainerName + "." + reference.EntityKey.EntitySetName, originalKeys), out oldEntity);
                        //WRITE ORIGINAL VALUES, ONLY IF WE GOT BACK IT SUCCESSFULLY
                        entity.GetType().GetProperties().Where(y => y.GetCustomAttributes(true).Any(z => z is EntityLabelAttribute)).ToList().ForEach(r => items.Add(new Tuple<string, object, object>(r.Name, gotOldEntity ? r.GetValue(oldEntity) : "", r.GetValue(entity))));
                    }
                    else if (oce.State == EntityState.Deleted)
                    {
                        entity.GetType().GetProperties().Where(y => y.GetCustomAttributes(true).Any(z => z is EntityLabelAttribute)).ToList().ForEach(r => items.Add(new Tuple<string, object, object>(r.Name, r.GetValue(entity), "")));
                    }
                    else
                    {
                        //IF NOT, WRITE EMPTY STRINGS FOR ORIGINAL VALUES
                        entity.GetType().GetProperties().Where(y => y.GetCustomAttributes(true).Any(z => z is EntityLabelAttribute)).ToList().ForEach(r => items.Add(new Tuple<string, object, object>(r.Name, "", r.GetValue(entity))));
                    }
                }
            }
            return items;
        }

        private void PrepareCommand(DbCommand command, string detail, string changingUser)
        {
            command.CommandText = this.TransactionHistoryLogCommandString + command.CommandText;
            command.Parameters.AddRange(this.CreateLogParameters(detail, changingUser));
        }

        /// <summary>
        /// Logs the entire transaction(a single call to SaveChanges)        
        /// </summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="interceptionContext"></param>
        /// <param name="command"></param>
        //SaveChanges might call multiple interception methods(NonQueryExecuting,ScalarExecuting,ReaderExecuting)
        //Once Log method is called by any of these three methods, the other methods should not Log
        //This is achieved by storing the dbcontext on the first call, and on subsequent call, check if it
        //is the same dbcontext that is being used, if it is, logged is set to true and this method exits. This 
        //process is encapsulated in ResetCurrentInterceptionContext method
        private void Log<TResult>(DbCommandInterceptionContext<TResult> interceptionContext, DbCommand command)
        {
            if (this.IsTransactionHistoryLogCommand(command))
            {
                return;
            }

            if (!this.IsDML(command))
            {
                return;
            }

            this.ResetCurrentInterceptionContext(interceptionContext);

            if (this.logged)
            {
                return;
            }

            var securityContext =  SecurityContext.Current;

            this.PrepareCommand(command, this.GetTransactionLogDetail(interceptionContext), securityContext.UserName);

            this.logged = true;
        }

        //Stores the first dbcontext off interceptioncontext on fist call to log method,
        //this dbcontext is checked in subsequent calls to check if it the same one, if same
        //logged is set to true
        private void ResetCurrentInterceptionContext(DbInterceptionContext interceptionContext)
        {
            if (this.commandInterceptionContext == null)
            {
                this.commandInterceptionContext = interceptionContext.DbContexts.First();
                return;
            }

            if (!interceptionContext.DbContexts.Contains(this.commandInterceptionContext, ReferenceEquals))
            {
                this.logged = false;
                this.commandInterceptionContext = interceptionContext.DbContexts.First();
            }
        }

        public override void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
        {
            this.Log(interceptionContext, command);
            base.NonQueryExecuting(command, interceptionContext);
        }

        public override void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext)
        {
            this.Log(interceptionContext, command);
            base.ScalarExecuting(command, interceptionContext);
        }

        public override void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
        {
            this.Log(interceptionContext, command);
            base.ReaderExecuting(command, interceptionContext);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    if (commandInterceptionContext != null)
                    {
                        commandInterceptionContext.Dispose();
                        commandInterceptionContext = null;
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~TransactionHistoryLogInterceptor() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        void IDisposable.Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
