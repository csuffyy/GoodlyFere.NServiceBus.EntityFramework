#region License

// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SagaPersister.cs">
//  Copyright 2015 Benjamin S. Ramey
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
// </copyright>
// <created>03/25/2015 9:51 AM</created>
// <updated>03/31/2015 12:55 PM by Ben Ramey</updated>
// --------------------------------------------------------------------------------------------------------------------

#endregion

#region Usings

using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Core;
using System.Data.Entity.Core.Objects.DataClasses;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Linq.Expressions;
using GoodlyFere.NServiceBus.EntityFramework.Exceptions;
using GoodlyFere.NServiceBus.EntityFramework.Interfaces;
using GoodlyFere.NServiceBus.EntityFramework.SharedDbContext;
using NServiceBus.Logging;
using NServiceBus.Saga;

#endregion

namespace GoodlyFere.NServiceBus.EntityFramework.SagaStorage
{
    public class SagaPersister : ISagaPersister
    {
        private static readonly ILog Logger = LogManager.GetLogger<SagaPersister>();
        private readonly IDbContextProvider _dbContextProvider;
        private ISagaDbContext _dbContext;

        public SagaPersister(IDbContextProvider dbContextProvider)
        {
            Logger.Debug("Initiating SagaPersister");

            if (dbContextProvider == null)
            {
                throw new ArgumentNullException("dbContextProvider");
            }

            _dbContextProvider = dbContextProvider;
        }

        private ISagaDbContext DbContext
        {
            get
            {
                return _dbContext ?? (_dbContext = _dbContextProvider.GetSagaDbContext());
            }
        }

        public void Complete(IContainSagaData saga)
        {
            Logger.Debug("Completing saga");

            if (saga == null)
            {
                Logger.Debug("Saga is null!  Throwing exception");
                throw new ArgumentNullException("saga");
            }

            Type sagaType = GetSagaType(saga);
            ThrowIfDbContextDoesNotHaveSagaDbSet(sagaType);

            Logger.DebugFormat("Found DbSet on DbContext for saga type {0}.", sagaType.FullName);

            DbEntityEntry entry = DbContext.Entry(saga);
            if (entry.State == EntityState.Detached)
            {
                Logger.Warn("Saga is detached from context!  Throwing exception");
                throw new DeletingDetachedEntityException();
            }

            Logger.Debug("Reloading saga entry, setting state to Deleted, saving changes on DbContext.");
            entry.Reload(); // avoid concurrency issues since we're deleting
            entry.State = EntityState.Deleted;
            DbContext.SaveChanges();
        }

        public TSagaData Get<TSagaData>(Guid sagaId) where TSagaData : IContainSagaData
        {
            Logger.DebugFormat("Getting saga with ID {0}", sagaId);

            Type sagaType = typeof(TSagaData);
            ThrowIfDbContextDoesNotHaveSagaDbSet(sagaType);

            if (sagaId == Guid.Empty)
            {
                Logger.WarnFormat("Saga ID is empty!  Throwing an exception.");
                throw new ArgumentException("sagaId cannot be empty.", "sagaId");
            }

            Logger.DebugFormat("Finding saga with ID {0}", sagaId);
            DbSet set = DbContext.Set(sagaType);
            object result = set.Find(sagaId);
            Logger.DebugFormat("Found saga? {0}", result != null);

            return (TSagaData)(result ?? default(TSagaData));
        }

        public TSagaData Get<TSagaData>(string propertyName, object propertyValue) where TSagaData : IContainSagaData
        {
            Logger.DebugFormat("Getting saga property {0} having value {1}", propertyName, propertyValue);

            Type sagaType = typeof(TSagaData);
            ThrowIfDbContextDoesNotHaveSagaDbSet(sagaType);

            if (string.IsNullOrEmpty(propertyName))
            {
                Logger.Warn("Property name is null!  Throwing exception.");
                throw new ArgumentNullException("propertyName");
            }

            Logger.Debug("Building expression to query for the property name and value.");
            ParameterExpression param = Expression.Parameter(sagaType, "sagaData");
            Expression<Func<TSagaData, bool>> filter = Expression.Lambda<Func<TSagaData, bool>>(
                Expression.MakeBinary(
                    ExpressionType.Equal,
                    Expression.Property(param, propertyName),
                    Expression.Constant(propertyValue)),
                param);

            IQueryable setQueryable = DbContext.Set(sagaType).AsQueryable();
            IQueryable result = setQueryable
                .Provider
                .CreateQuery(
                    Expression.Call(
                        typeof(Queryable),
                        "Where",
                        new[] { sagaType },
                        setQueryable.Expression,
                        Expression.Quote(filter)));

            Logger.Debug("Finding results with expression-built query");
            List<object> results = result.ToListAsync().Result;
            if (results.Any())
            {
                Logger.Debug("Results found! Taking first one");
                return (TSagaData)results.First();
            }

            Logger.Debug("No results found! Returning default value.");
            return default(TSagaData);
        }

        public void Save(IContainSagaData saga)
        {
            Logger.Debug("Saving saga");

            if (saga == null)
            {
                Logger.WarnFormat("Saga is null!  Throwing exception");
                throw new ArgumentNullException("saga");
            }

            Type sagaType = GetSagaType(saga);
            ThrowIfDbContextDoesNotHaveSagaDbSet(sagaType);

            Logger.Debug("Adding saga to DbContext");
            DbSet sagaSet = DbContext.Set(sagaType);
            sagaSet.Add(saga);
            DbContext.SaveChanges();
        }

        public void Update(IContainSagaData saga)
        {
            Logger.Debug("Updating saga");

            if (saga == null)
            {
                Logger.WarnFormat("Saga is null!  Throwing exception");
                throw new ArgumentNullException("saga");
            }

            ThrowIfDbContextDoesNotHaveSagaDbSet(saga);

            try
            {
                Logger.DebugFormat("Trying to update saga with ID {0}", saga.Id);
                DbEntityEntry entry = DbContext.Entry((object)saga);
                if (entry.State == EntityState.Detached)
                {
                    Logger.Warn("Saga is detached from context!  Throwing exception");
                    throw new UpdatingDetachedEntityException();
                }

                if (entry.State == EntityState.Modified)
                {
                    Logger.Debug("Saga is in Modified state! Saving changes.");
                    DbContext.SaveChanges();
                }
            }
            catch (DbUpdateConcurrencyException ex)
            {
                Logger.Warn("Got DbUpdateConcurrencyException!");
                if (IsOptimisticConcurrencyException(ex))
                {
                    Logger.Debug("It's an optimistic concurrency exception.");
                    ReconcileConcurrencyIssues(saga);
                    DbContext.SaveChanges();
                }
                else
                {
                    throw;
                }
            }
        }

        private static Type GetSagaType(IContainSagaData saga)
        {
            Type sagaType = saga.GetType();
            Logger.DebugFormat("Saga is of type {0}", sagaType.FullName);

            // if this class is the dynamic proxy type inserted by EF
            // then we want the base type (actual saga data type) not the dynamic proxy
            if (typeof(IEntityWithChangeTracker).IsAssignableFrom(sagaType))
            {
                Logger.Debug("Saga is an EF dynamic proxy, getting base type.");
                sagaType = sagaType.BaseType;
            }

            return sagaType;
        }

        private static bool IsOptimisticConcurrencyException(DbUpdateConcurrencyException ex)
        {
            return ex.InnerException is OptimisticConcurrencyException;
        }

        private void ReconcileConcurrencyIssues(IContainSagaData saga)
        {
            Logger.Debug("Trying to reconcile concurrency issues.");
            DbEntityEntry entry = DbContext.Entry((object)saga);

            // 1. get the names of properties that have changes.
            List<string> changedPropertyNames = entry.OriginalValues.PropertyNames
                .Where(pn => entry.OriginalValues[pn] != entry.CurrentValues[pn])
                .ToList();
            Logger.DebugFormat("Changed properties are {0}.", string.Join(",", changedPropertyNames));

            // 2. collect values of changed properties
            Dictionary<string, object> changedValues = changedPropertyNames
                .ToDictionary(pn => pn, pn => entry.CurrentValues[pn]);

            // 3. reload values from database
            Logger.Debug("Reloading entry.");
            entry.Reload();

            // 4. resave only the changes values
            foreach (var changedValue in changedValues)
            {
                Logger.DebugFormat("Updating property {0} with changed value.", changedValue.Key);
                var property = entry.Property(changedValue.Key);
                property.CurrentValue = changedValue.Value;
                property.IsModified = true;
            }
        }

        private void ThrowIfDbContextDoesNotHaveSagaDbSet(Type sagaType)
        {
            if (!DbContext.HasSet(sagaType))
            {
                Logger.WarnFormat(
                    "DbContext does not have a DbSet of saga type {0}, throwing exception.",
                    sagaType.FullName);
                throw new SagaDbSetMissingException(DbContext.GetType(), sagaType);
            }
        }

        private void ThrowIfDbContextDoesNotHaveSagaDbSet(IContainSagaData saga)
        {
            Type sagaType = GetSagaType(saga);

            if (!DbContext.HasSet(sagaType))
            {
                Logger.WarnFormat(
                    "DbContext does not have a DbSet of saga type {0}, throwing exception.",
                    sagaType.FullName);
                throw new SagaDbSetMissingException(DbContext.GetType(), sagaType);
            }
        }
    }
}