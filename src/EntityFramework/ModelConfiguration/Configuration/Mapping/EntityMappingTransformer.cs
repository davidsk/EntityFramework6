﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.ModelConfiguration.Configuration.Mapping
{
    using System.Collections.Generic;
    using System.Data.Entity.Core.Mapping;
    using System.Data.Entity.Core.Metadata.Edm;
    using System.Data.Entity.ModelConfiguration.Configuration.Properties.Primitive;
    using System.Data.Entity.ModelConfiguration.Edm;
    using System.Data.Entity.Utilities;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;

    internal static class TablePrimitiveOperations
    {
        public static void AddColumn(EntityType table, EdmProperty column)
        {
            DebugCheck.NotNull(table);
            DebugCheck.NotNull(column);

            if (!table.Properties.Contains(column))
            {
                var configuration = column.GetConfiguration() as PrimitivePropertyConfiguration;

                if ((configuration == null)
                    || string.IsNullOrWhiteSpace(configuration.ColumnName))
                {
                    var preferredName = column.GetPreferredName() ?? column.Name;
                    column.SetUnpreferredUniqueName(column.Name);
                    column.Name = table.Properties.UniquifyName(preferredName);
                }

                table.AddMember(column);
            }
        }

        public static EdmProperty RemoveColumn(EntityType table, EdmProperty column)
        {
            DebugCheck.NotNull(table);
            DebugCheck.NotNull(column);

            if (!column.IsPrimaryKeyColumn)
            {
                table.RemoveMember(column);
            }

            return column;
        }

        public static EdmProperty IncludeColumn(
            EntityType table, EdmProperty templateColumn, Func<EdmProperty, bool> isCompatible, bool useExisting)
        {
            DebugCheck.NotNull(table);
            DebugCheck.NotNull(templateColumn);

            var existingColumn = table.Properties.SingleOrDefault(isCompatible);

            if (existingColumn == null)
            {
                templateColumn = templateColumn.Clone();
            }
            else if (!useExisting
                     && !existingColumn.IsPrimaryKeyColumn)
            {
                templateColumn = templateColumn.Clone();
            }
            else
            {
                templateColumn = existingColumn;
            }

            AddColumn(table, templateColumn);

            return templateColumn;
        }

        public static Func<EdmProperty, bool> GetNameMatcher(string name)
        {
            return c => string.Equals(c.Name, name, StringComparison.Ordinal);
        }
    }

    internal static class ForeignKeyPrimitiveOperations
    {
        public static void UpdatePrincipalTables(
            DbDatabaseMapping databaseMapping,
            EntityType entityType,
            EntityType fromTable,
            EntityType toTable,
            bool isMappingAnyInheritedProperty)
        {
            DebugCheck.NotNull(databaseMapping);
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);

            if (fromTable != toTable)
            {
                // Update the principal tables for associations/fks defined on the exact given entity type
                // In this case they need to be moved to the appropriate table, but not removed
                UpdatePrincipalTables(databaseMapping, toTable, entityType, removeFks: false);

                if (isMappingAnyInheritedProperty)
                {
                    // if mapping inherited properties, remove FKs that have the base type as the principal
                    UpdatePrincipalTables(databaseMapping, toTable, (EntityType)entityType.BaseType, removeFks: true);
                }
            }
        }

        private static void UpdatePrincipalTables(
            DbDatabaseMapping databaseMapping, EntityType toTable, EntityType entityType, bool removeFks)
        {
            foreach (var associationType in databaseMapping.Model.AssociationTypes
                                                           .Where(
                                                               at =>
                                                               at.SourceEnd.GetEntityType().Equals(entityType)
                                                               || at.TargetEnd.GetEntityType().Equals(entityType)))
            {
                UpdatePrincipalTables(databaseMapping, toTable, removeFks, associationType, entityType);
            }
        }

        [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity")]
        private static void UpdatePrincipalTables(
            DbDatabaseMapping databaseMapping, EntityType toTable, bool removeFks,
            AssociationType associationType, EntityType et)
        {
            AssociationEndMember principalEnd, dependentEnd;
            var endsToCheck = new List<AssociationEndMember>();
            if (associationType.TryGuessPrincipalAndDependentEnds(out principalEnd, out dependentEnd))
            {
                endsToCheck.Add(principalEnd);
            }
            else if (associationType.SourceEnd.RelationshipMultiplicity == RelationshipMultiplicity.Many
                     && associationType.TargetEnd.RelationshipMultiplicity == RelationshipMultiplicity.Many)
            {
                // many to many consider both ends
                endsToCheck.Add(associationType.SourceEnd);
                endsToCheck.Add(associationType.TargetEnd);
            }
            else
            {
                // 1:1 and 0..1:0..1
                endsToCheck.Add(associationType.SourceEnd);
            }

            foreach (var end in endsToCheck)
            {
                if (end.GetEntityType() == et)
                {
                    IEnumerable<KeyValuePair<EntityType, IEnumerable<EdmProperty>>> dependentTableInfos;
                    if (associationType.Constraint != null)
                    {
                        var originalDependentType = associationType.GetOtherEnd(end).GetEntityType();
                        var allDependentTypes = databaseMapping.Model.GetSelfAndAllDerivedTypes(originalDependentType);

                        dependentTableInfos =
                            allDependentTypes.Select(t => databaseMapping.GetEntityTypeMapping(t)).Where(
                                dm => dm != null)
                                             .SelectMany(
                                                 dm => dm.MappingFragments
                                                         .Where(
                                                             tmf => associationType.Constraint.ToProperties
                                                                                   .All(
                                                                                       p =>
                                                                                       tmf.ColumnMappings.Any(
                                                                                           pm => pm.PropertyPath.First() == p))))
                                             .Distinct((f1, f2) => f1.Table == f2.Table)
                                             .Select(
                                                 df =>
                                                 new KeyValuePair<EntityType, IEnumerable<EdmProperty>>(
                                                     df.Table,
                                                     df.ColumnMappings.Where(
                                                         pm =>
                                                         associationType.Constraint.ToProperties.Contains(
                                                             pm.PropertyPath.First())).Select(
                                                                 pm => pm.ColumnProperty)));
                    }
                    else
                    {
                        // IA
                        var associationSetMapping =
                            databaseMapping.EntityContainerMappings
                                           .Single().AssociationSetMappings
                                           .Single(asm => asm.AssociationSet.ElementType == associationType);

                        var dependentTable = associationSetMapping.Table;
                        var propertyMappings = associationSetMapping.SourceEndMapping.EndMember == end
                                                   ? associationSetMapping.SourceEndMapping.PropertyMappings
                                                   : associationSetMapping.TargetEndMapping.PropertyMappings;
                        var dependentColumns = propertyMappings.Select(pm => pm.ColumnProperty);

                        dependentTableInfos = new[]
                            {
                                new KeyValuePair
                                    <EntityType, IEnumerable<EdmProperty>>(
                                    dependentTable, dependentColumns)
                            };
                    }

                    foreach (var tableInfo in dependentTableInfos)
                    {
                        foreach (
                            var fk in
                                tableInfo.Key.ForeignKeyBuilders.Where(
                                    fk => fk.DependentColumns.SequenceEqual(tableInfo.Value)).ToArray(
                                    ))
                        {
                            if (removeFks)
                            {
                                tableInfo.Key.RemoveForeignKey(fk);
                            }
                            else if (fk.GetAssociationType() == null || fk.GetAssociationType() == associationType)
                            {
                                fk.PrincipalTable = toTable;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Moves a foreign key constraint from oldTable to newTable and updates column references
        /// </summary>
        private static void MoveForeignKeyConstraint(
            EntityType fromTable, EntityType toTable, ForeignKeyBuilder fk)
        {
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);
            DebugCheck.NotNull(fk);

            fromTable.RemoveForeignKey(fk);

            // Only move it to the new table if the destination is not the principal table or if all dependent columns are not FKs
            // Otherwise you end up with an FK from the PKs to the PKs of the same table
            if (fk.PrincipalTable != toTable
                || !fk.DependentColumns.All(c => c.IsPrimaryKeyColumn))
            {
                // Make sure all the dependent columns refer to columns in the newTable
                var oldColumns = fk.DependentColumns.ToArray();

                var dependentColumns
                    = GetDependentColumns(oldColumns, toTable.Properties);

                if (!ContainsEquivalentForeignKey(toTable, fk.PrincipalTable, dependentColumns))
                {
                    toTable.AddForeignKey(fk);

                    fk.DependentColumns = dependentColumns;
                }
            }
        }

        private static void CopyForeignKeyConstraint(EdmModel database, EntityType toTable, ForeignKeyBuilder fk, 
            Func<EdmProperty, EdmProperty> selector = null)
        {
            DebugCheck.NotNull(toTable);
            DebugCheck.NotNull(fk);

            var newFk
                = new ForeignKeyBuilder(
                    database,
                    database.EntityTypes.SelectMany(t => t.ForeignKeyBuilders).UniquifyName(fk.Name))
                    {
                        PrincipalTable = fk.PrincipalTable,
                        DeleteAction = fk.DeleteAction
                    };

            var dependentColumns = 
                GetDependentColumns(
                    selector != null
                        ? fk.DependentColumns.Select(selector)
                        : fk.DependentColumns,
                    toTable.Properties);

            if (!ContainsEquivalentForeignKey(toTable, newFk.PrincipalTable, dependentColumns))
            {
                toTable.AddForeignKey(newFk);

                newFk.DependentColumns = dependentColumns;
            }
        }

        private static bool ContainsEquivalentForeignKey(
            EntityType dependentTable, EntityType principalTable, IEnumerable<EdmProperty> columns)
        {
            return dependentTable.ForeignKeyBuilders
                                 .Any(
                                     fk => fk.PrincipalTable == principalTable
                                           && fk.DependentColumns.SequenceEqual(columns));
        }

        private static IList<EdmProperty> GetDependentColumns(
            IEnumerable<EdmProperty> sourceColumns,
            IEnumerable<EdmProperty> destinationColumns)
        {
            return sourceColumns
                .Select(
                    sc =>
                    destinationColumns.SingleOrDefault(
                        dc => string.Equals(dc.Name, sc.Name, StringComparison.Ordinal))
                    ??
                    destinationColumns.Single(
                        dc => string.Equals(dc.GetUnpreferredUniqueName(), sc.Name, StringComparison.Ordinal))
                )
                .ToList();
        }

        private static IEnumerable<ForeignKeyBuilder> FindAllForeignKeyConstraintsForColumn(
            EntityType fromTable, EntityType toTable, EdmProperty column)
        {
            return fromTable
                .ForeignKeyBuilders
                .Where(
                    fk => fk.DependentColumns.Contains(column) &&
                          fk.DependentColumns.All(
                              c => toTable.Properties.Any(
                                  nc =>
                                  string.Equals(nc.Name, c.Name, StringComparison.Ordinal)
                                  || string.Equals(nc.GetUnpreferredUniqueName(), c.Name, StringComparison.Ordinal))));
        }

        public static void CopyAllForeignKeyConstraintsForColumn(
            EdmModel database, EntityType fromTable, EntityType toTable,
            EdmProperty column, EdmProperty movedColumn)
        {
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);
            DebugCheck.NotNull(column);

            FindAllForeignKeyConstraintsForColumn(fromTable, toTable, column)
                .ToArray()
                .Each(fk => CopyForeignKeyConstraint(database, toTable, fk, 
                                c => c == column ? movedColumn : c));
        }

        public static void MoveAllDeclaredForeignKeyConstraintsForPrimaryKeyColumns(
            EntityType entityType, EntityType fromTable, EntityType toTable)
        {
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);

            foreach (var column in fromTable.KeyProperties)
            {
                FindAllForeignKeyConstraintsForColumn(fromTable, toTable, column)
                    .ToArray()
                    .Each(
                        fk =>
                            {
                                var at = fk.GetAssociationType();
                                if (at != null
                                    && at.Constraint.ToRole.GetEntityType() == entityType
                                    && !fk.GetIsTypeConstraint())
                                {
                                    MoveForeignKeyConstraint(fromTable, toTable, fk);
                                }
                            });
            }
        }

        public static void CopyAllForeignKeyConstraintsForPrimaryKeyColumns(
            EdmModel database, EntityType fromTable, EntityType toTable)
        {
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);

            foreach (var column in fromTable.KeyProperties)
            {
                FindAllForeignKeyConstraintsForColumn(fromTable, toTable, column)
                    .ToArray()
                    .Each(
                        fk =>
                            {
                                if (!fk.GetIsTypeConstraint())
                                {
                                    CopyForeignKeyConstraint(database, toTable, fk);
                                }
                            });
            }
        }

        /// <summary>
        /// Move any FK constraints that are now completely in newTable and used to refer to oldColumn
        /// </summary>
        public static void MoveAllForeignKeyConstraintsForColumn(
            EntityType fromTable, EntityType toTable, EdmProperty column)
        {
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);
            DebugCheck.NotNull(column);

            FindAllForeignKeyConstraintsForColumn(fromTable, toTable, column)
                .ToArray()
                .Each(fk => { MoveForeignKeyConstraint(fromTable, toTable, fk); });
        }

        public static void RemoveAllForeignKeyConstraintsForColumn(EntityType table, EdmProperty column)
        {
            DebugCheck.NotNull(table);
            DebugCheck.NotNull(column);

            table.ForeignKeyBuilders
                 .Where(fk => fk.DependentColumns.Contains(column))
                 .ToArray()
                 .Each(table.RemoveForeignKey);
        }
    }

    internal static class TableOperations
    {
        public static EdmProperty CopyColumnAndAnyConstraints(
            EdmModel database,
            EntityType fromTable,
            EntityType toTable,
            EdmProperty column,
            Func<EdmProperty, bool> isCompatible,
            bool useExisting)
        {
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);
            DebugCheck.NotNull(column);

            var movedColumn = column;

            if (fromTable != toTable)
            {
                movedColumn = TablePrimitiveOperations.IncludeColumn(toTable, column, isCompatible, useExisting);
                if (!movedColumn.IsPrimaryKeyColumn)
                {
                    ForeignKeyPrimitiveOperations.CopyAllForeignKeyConstraintsForColumn(
                        database, fromTable, toTable, column, movedColumn);
                }
            }

            return movedColumn;
        }

        public static EdmProperty MoveColumnAndAnyConstraints(
            EntityType fromTable, EntityType toTable, EdmProperty column, bool useExisting)
        {
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);
            DebugCheck.NotNull(column);

            var movedColumn = column;

            if (fromTable != toTable)
            {
                movedColumn = TablePrimitiveOperations.IncludeColumn(
                    toTable, column, TablePrimitiveOperations.GetNameMatcher(column.Name), useExisting);
                TablePrimitiveOperations.RemoveColumn(fromTable, column);
                ForeignKeyPrimitiveOperations.MoveAllForeignKeyConstraintsForColumn(fromTable, toTable, column);
            }

            return movedColumn;
        }
    }

    internal static class EntityMappingOperations
    {
        public static StorageMappingFragment CreateTypeMappingFragment(
            StorageEntityTypeMapping entityTypeMapping, StorageMappingFragment templateFragment, EntitySet tableSet)
        {
            var fragment = new StorageMappingFragment(tableSet, entityTypeMapping, false);

            entityTypeMapping.AddFragment(fragment);

            // Move all PK mappings to the extra fragment
            foreach (
                var pkPropertyMapping in templateFragment.ColumnMappings.Where(pm => pm.ColumnProperty.IsPrimaryKeyColumn))
            {
                CopyPropertyMappingToFragment(
                    pkPropertyMapping, fragment, TablePrimitiveOperations.GetNameMatcher(pkPropertyMapping.ColumnProperty.Name),
                    useExisting: true);

            }
            return fragment;
        }

        private static void UpdatePropertyMapping(
            DbDatabaseMapping databaseMapping,
            ColumnMappingBuilder propertyMappingBuilder,
            EntityType fromTable,
            EntityType toTable,
            bool useExisting)
        {
            propertyMappingBuilder.ColumnProperty
                = TableOperations.CopyColumnAndAnyConstraints(
                    databaseMapping.Database, fromTable, toTable, propertyMappingBuilder.ColumnProperty, GetPropertyPathMatcher(databaseMapping, propertyMappingBuilder), useExisting);

            propertyMappingBuilder.SyncNullabilityCSSpace();
        }

        private static Func<EdmProperty, bool> GetPropertyPathMatcher(DbDatabaseMapping databaseMapping, ColumnMappingBuilder propertyMappingBuilder)
        {
            return c =>
                databaseMapping.EntityContainerMappings.Single()
                    .EntitySetMappings.SelectMany(esm => esm.EntityTypeMappings)
                    .SelectMany(etm => etm.MappingFragments)
                    .SelectMany(etmf => etmf.ColumnMappings)
                    .Any(
                        pm => pm.PropertyPath.SequenceEqual(propertyMappingBuilder.PropertyPath)
                              && pm.ColumnProperty == c);
        }

        public static void UpdatePropertyMappings(
            DbDatabaseMapping databaseMapping,
            EntityType fromTable,
            StorageMappingFragment fragment,
            bool useExisting)
        {
            // move the column from the fromTable to the table in fragment
            if (fromTable != fragment.Table)
            {
                fragment.ColumnMappings.Each(
                    pm => UpdatePropertyMapping(databaseMapping, pm, fromTable, fragment.Table, useExisting));
            }
        }

        public static void MovePropertyMapping(
            DbDatabaseMapping databaseMapping,
            StorageMappingFragment fromFragment,
            StorageMappingFragment toFragment,
            ColumnMappingBuilder propertyMappingBuilder,
            bool requiresUpdate,
            bool useExisting)
        {
            // move the column from the formTable to the table in fragment
            if (requiresUpdate && fromFragment.Table != toFragment.Table)
            {
                UpdatePropertyMapping(databaseMapping, propertyMappingBuilder, fromFragment.Table, toFragment.Table, useExisting);
            }

            // move the propertyMapping
            fromFragment.RemoveColumnMapping(propertyMappingBuilder);
            toFragment.AddColumnMapping(propertyMappingBuilder);
        }

        public static void CopyPropertyMappingToFragment(
            ColumnMappingBuilder propertyMappingBuilder, StorageMappingFragment fragment,
            Func<EdmProperty, bool> isCompatible, bool useExisting)
        {
            // Ensure column is in the fragment's table
            var column = TablePrimitiveOperations.IncludeColumn(fragment.Table, propertyMappingBuilder.ColumnProperty, isCompatible, useExisting);

            // Add the property mapping
            fragment.AddColumnMapping(
                new ColumnMappingBuilder(column, propertyMappingBuilder.PropertyPath));
        }

        public static void UpdateConditions(
            EdmModel database, EntityType fromTable, StorageMappingFragment fragment)
        {
            // move the condition's column from the formTable to the table in fragment
            if (fromTable != fragment.Table)
            {
                fragment.ColumnConditions.Each(
                    cc =>
                    {
                        cc.ColumnProperty
                            = TableOperations.CopyColumnAndAnyConstraints(
                                database, fromTable, fragment.Table, cc.ColumnProperty,
                                TablePrimitiveOperations.GetNameMatcher(cc.ColumnProperty.Name),
                                useExisting: true);
                    });
            }
        }
    }

    internal static class AssociationMappingOperations
    {
        private static void MoveAssociationSetMappingDependents(
            StorageAssociationSetMapping associationSetMapping,
            StorageEndPropertyMapping dependentMapping,
            EntitySet toSet,
            bool useExistingColumns)
        {
            DebugCheck.NotNull(associationSetMapping);
            DebugCheck.NotNull(dependentMapping);
            DebugCheck.NotNull(toSet);

            var toTable = toSet.ElementType;

            dependentMapping.PropertyMappings.Each(
                pm =>
                    {
                        var oldColumn = pm.ColumnProperty;

                        pm.ColumnProperty
                            = TableOperations.MoveColumnAndAnyConstraints(
                                associationSetMapping.Table, toTable, oldColumn, useExistingColumns);

                        associationSetMapping.ColumnConditions
                                             .Where(cc => cc.ColumnProperty == oldColumn)
                                             .Each(cc => cc.ColumnProperty = pm.ColumnProperty);
                    });

            associationSetMapping.StoreEntitySet = toSet;
        }

        public static void MoveAllDeclaredAssociationSetMappings(
            DbDatabaseMapping databaseMapping,
            EntityType entityType,
            EntityType fromTable,
            EntityType toTable,
            bool useExistingColumns)
        {
            DebugCheck.NotNull(databaseMapping);
            DebugCheck.NotNull(entityType);
            DebugCheck.NotNull(fromTable);
            DebugCheck.NotNull(toTable);

            foreach (
                var associationSetMapping in
                    databaseMapping.EntityContainerMappings.SelectMany(asm => asm.AssociationSetMappings)
                                   .Where(
                                       a =>
                                       a.Table == fromTable &&
                                       (a.AssociationSet.ElementType.SourceEnd.GetEntityType() == entityType ||
                                        a.AssociationSet.ElementType.TargetEnd.GetEntityType() == entityType)).ToArray())
            {
                AssociationEndMember _, dependentEnd;
                if (!associationSetMapping.AssociationSet.ElementType.TryGuessPrincipalAndDependentEnds(
                        out _, out dependentEnd))
                {
                    dependentEnd = associationSetMapping.AssociationSet.ElementType.TargetEnd;
                }

                if (dependentEnd.GetEntityType() == entityType)
                {
                    var dependentMapping
                        = dependentEnd == associationSetMapping.TargetEndMapping.EndMember
                              ? associationSetMapping.SourceEndMapping
                              : associationSetMapping.TargetEndMapping;

                    MoveAssociationSetMappingDependents(
                        associationSetMapping,
                        dependentMapping,
                        databaseMapping.Database.GetEntitySet(toTable),
                        useExistingColumns);
                
                    var principalMapping
                        = dependentMapping == associationSetMapping.TargetEndMapping
                              ? associationSetMapping.SourceEndMapping
                              : associationSetMapping.TargetEndMapping;

                    principalMapping.PropertyMappings.Each(
                        pm =>
                            {
                                if (pm.ColumnProperty.DeclaringType != toTable)
                                {
                                    pm.ColumnProperty
                                        = toTable.Properties.Single(
                                            p => string.Equals(
                                                p.GetPreferredName(),
                                                pm.ColumnProperty.GetPreferredName(),
                                                StringComparison.Ordinal));
                                }
                            });
                }
            }
        }
    }

    internal static class DatabaseOperations
    {
        public static void AddTypeConstraint(
            EdmModel database,
            EntityType entityType,
            EntityType principalTable,
            EntityType dependentTable,
            bool isSplitting)
        {
            DebugCheck.NotNull(principalTable);
            DebugCheck.NotNull(dependentTable);
            DebugCheck.NotNull(entityType);

            var foreignKeyConstraintMetadata
                = new ForeignKeyBuilder(
                    database, String.Format(
                        CultureInfo.InvariantCulture,
                        "{0}_TypeConstraint_From_{1}_To_{2}",
                        entityType.Name,
                        principalTable.Name,
                        dependentTable.Name))
                    {
                        PrincipalTable = principalTable
                    };

            dependentTable.AddForeignKey(foreignKeyConstraintMetadata);

            if (isSplitting)
            {
                foreignKeyConstraintMetadata.SetIsSplitConstraint();
            }
            else
            {
                foreignKeyConstraintMetadata.SetIsTypeConstraint();
            }

            foreignKeyConstraintMetadata.DependentColumns = dependentTable.Properties.Where(c => c.IsPrimaryKeyColumn);

            //If "DbStoreGeneratedPattern.Identity" was copied from the parent table, it should be removed
            dependentTable.Properties.Where(c => c.IsPrimaryKeyColumn).Each(c => c.RemoveStoreGeneratedIdentityPattern());
        }
    }
}
