using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Core;
using System.Data.Entity.Core.Common;
using System.Data.Entity.Core.Metadata.Edm;
using System.Data.Entity.Core.Objects;
using System.Data.Entity.Core.Objects.DataClasses;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

public static class EntityFrameworkExtensions
{
    public static EntityState GetEntityState(this ObjectContext context, EntityKey key)
    {
        ObjectStateEntry entry = context.ObjectStateManager.GetObjectStateEntry(key);
        return entry.State;
    }

    public static string GetFullEntitySetName(this EntityKey key)
    {
        return key.EntityContainerName + "." + key.EntitySetName;
    }

    public static IEntityWithKey GetEntityByKey(this ObjectContext context, EntityKey key)
    {
        return (IEntityWithKey)context.ObjectStateManager.GetObjectStateEntry(key).Entity;
    }

    public static IExtendedDataRecord UsableValues(this ObjectStateEntry entry)
    {
        switch (entry.State)
        {
            case EntityState.Detached:
            case EntityState.Unchanged:
            case EntityState.Added:
            case EntityState.Modified:
                return entry.CurrentValues;
            case EntityState.Deleted:
                return (IExtendedDataRecord)entry.OriginalValues;
            default:
                throw new InvalidOperationException("This entity state should not exist.");
        }
    }

    public static EdmType EdmType(this ObjectStateEntry entry)
    {
        return entry.UsableValues().DataRecordInfo.RecordType.EdmType;
    }

    public static bool IsManyToMany(this AssociationType associationType)
    {
        foreach (RelationshipEndMember relationshipEndMember in associationType.RelationshipEndMembers)
        {
            if (relationshipEndMember.RelationshipMultiplicity != RelationshipMultiplicity.Many)
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsRelationshipForKey(this ObjectStateEntry entry, EntityKey key)
    {
        if (!entry.IsRelationship)
        {
            return false;
        }
        return (EntityKey)((IDataRecord)entry.UsableValues())[0] == key || (EntityKey)((IDataRecord)entry.UsableValues())[1] == key;
    }

    public static EntityKey OtherEndKey(this ObjectStateEntry relationshipEntry, EntityKey thisEndKey)
    {
        Debug.Assert(relationshipEntry.IsRelationship);
        Debug.Assert(thisEndKey != (EntityKey)null);
        if ((EntityKey)((IDataRecord)relationshipEntry.UsableValues())[0] == thisEndKey)
        {
            return (EntityKey)((IDataRecord)relationshipEntry.UsableValues())[1];
        }
        if ((EntityKey)((IDataRecord)relationshipEntry.UsableValues())[1] == thisEndKey)
        {
            return (EntityKey)((IDataRecord)relationshipEntry.UsableValues())[0];
        }
        throw new InvalidOperationException("Neither end of the relationship contains the passed in key.");
    }

    public static string OtherEndRole(this ObjectStateEntry relationshipEntry, EntityKey thisEndKey)
    {
        Debug.Assert(relationshipEntry != null);
        Debug.Assert(relationshipEntry.IsRelationship);
        Debug.Assert(thisEndKey != (EntityKey)null);
        FieldMetadata fieldMetadata;
        if ((EntityKey)((IDataRecord)relationshipEntry.UsableValues())[0] == thisEndKey)
        {
            fieldMetadata = relationshipEntry.UsableValues().DataRecordInfo.FieldMetadata[1];
            return fieldMetadata.FieldType.Name;
        }
        if ((EntityKey)((IDataRecord)relationshipEntry.UsableValues())[1] == thisEndKey)
        {
            fieldMetadata = relationshipEntry.UsableValues().DataRecordInfo.FieldMetadata[0];
            return fieldMetadata.FieldType.Name;
        }
        throw new InvalidOperationException("Neither end of the relationship contains the passed in key.");
    }

    public static bool IsEntityReference(this IRelatedEnd relatedEnd)
    {
        Type relationshipType = relatedEnd.GetType();
        return relationshipType.GetGenericTypeDefinition() == typeof(EntityReference<>);
    }

    public static EntityKey GetEntityKey(this IRelatedEnd relatedEnd)
    {
        Debug.Assert(relatedEnd.IsEntityReference());
        Type relationshipType = relatedEnd.GetType();
        PropertyInfo pi = relationshipType.GetProperty("EntityKey");
        return (EntityKey)pi.GetValue(relatedEnd, null);
    }

    public static void SetEntityKey(this IRelatedEnd relatedEnd, EntityKey key)
    {
        Debug.Assert(relatedEnd.IsEntityReference());
        Type relationshipType = relatedEnd.GetType();
        PropertyInfo pi = relationshipType.GetProperty("EntityKey");
        pi.SetValue(relatedEnd, key, null);
    }

    public static bool Contains(this IRelatedEnd relatedEnd, EntityKey key)
    {
        foreach (object item in relatedEnd)
        {
            Debug.Assert(item is IEntityWithKey);
            if (((IEntityWithKey)item).EntityKey == key)
            {
                return true;
            }
        }
        return false;
    }

    public static bool TryGetObjectByKey(this ObjectContext context, ObjectStateEntry entry, IRelatedEnd relatedEnd, out object relatedEndEntity)
    {
        Debug.Assert(relatedEnd.IsEntityReference());
        AssociationSetEnd associationSetEnd = (from ase in ((AssociationSet)relatedEnd.RelationshipSet).AssociationSetEnds
                                               where ase.Name.Equals(relatedEnd.TargetRoleName)
                                               select ase).First();
        MetadataProperty metaDataProperty = (from mp in associationSetEnd.MetadataProperties
                                             where mp.Name.Equals("EntitySet")
                                             select mp).First();
        EntitySet entitySet = (EntitySet)metaDataProperty.GetType().GetProperty("Value").GetValue(metaDataProperty);
        IEnumerable<EntityKeyMember> keyMembers = from km in entitySet.ElementType.KeyMembers
                                                  select new EntityKeyMember(km.Name, entry.OriginalValues[km.Name]);
        return context.TryGetObjectByKey(new EntityKey(entitySet.EntityContainer.Name + "." + entitySet.Name, keyMembers), out relatedEndEntity);
    }
}
