﻿// *******************************************************************
//
//  Copyright (c) 2012-2015, Antmicro Ltd <antmicro.com>
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// *******************************************************************
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Antmicro.Migrant.VersionTolerance;
using Antmicro.Migrant.Customization;
using System.Collections.Concurrent;

namespace Antmicro.Migrant
{
    internal class TypeDescriptor
    {
        public static TypeDescriptor ReadFromStream(ObjectReader reader)
        {
            var result =  new TypeDescriptor();
            result.ReadTypeStamp(reader);
            return result;
        }

        public static TypeDescriptor CreateFromType(Type type)
        {
            TypeDescriptor value;
            if(!cache.TryGetValue(type, out value))
            {
                value = new TypeDescriptor();
                cache.AddOrUpdate(type, value, (k, v) => value);
                // we need to call init after creating empty `TypeDescriptor`
                // and putting it in `Cache` as field types can refer to the
                // cache
                value.Init(type);
            }

            return value;
        }

        public void ReadStructureStampIfNeeded(ObjectReader reader, VersionToleranceLevel versionToleranceLevel)
        {
            if(StampHelpers.IsStampNeeded(this, reader.TreatCollectionAsUserObject))
            {
                ReadStructureStamp(reader, versionToleranceLevel);
            }
        }

        public void WriteStructureStampIfNeeded(ObjectWriter writer)
        {
            if(StampHelpers.IsStampNeeded(this, writer.TreatCollectionAsUserObject))
            {
                WriteStructureStamp(writer);
            }
        }

        public void WriteTypeStamp(ObjectWriter writer)
        {
            writer.TouchAndWriteAssemblyId(TypeAssembly);
            writer.PrimitiveWriter.Write(GenericFullName);
            writer.PrimitiveWriter.Write(genericArguments.Count);
            foreach (var genericArgument in genericArguments)
            {
                writer.TouchAndWriteTypeId(genericArgument.UnderlyingType);
            }
        }

        public override int GetHashCode()
        {
            return UnderlyingType.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            var objAsTypeDescriptor = obj as TypeDescriptor;
            if(objAsTypeDescriptor != null)
            {
                return UnderlyingType == objAsTypeDescriptor.UnderlyingType;
            }

            return obj != null && obj.Equals(this);
        }

        public bool Equals(TypeDescriptor obj, VersionToleranceLevel versionToleranceLevel)
        {
            if(versionToleranceLevel.HasFlag(VersionToleranceLevel.AllowAssemblyVersionChange))
            {
                return obj.UnderlyingType.FullName == UnderlyingType.FullName
                    && obj.TypeAssembly.Equals(TypeAssembly, versionToleranceLevel);
            }

            return Equals(obj);
        }

        public TypeDescriptorCompareResult CompareWith(TypeDescriptor previous, VersionToleranceLevel versionToleranceLevel = 0)
        {
            var result = new TypeDescriptorCompareResult();

            var prevFields = previous.fields.ToDictionary(x => x.FullName, x => x);
            foreach(var field in fields.Where(f => !f.IsTransient))
            {
                FieldDescriptor currentField;
                if(!prevFields.TryGetValue(field.FullName, out currentField))
                {
                    // field is missing in the previous version of the class
                    result.FieldsAdded.Add(field);
                    continue;
                }
                // are the types compatible?
                var compareResult = currentField.CompareWith(field, versionToleranceLevel);
                if(compareResult != FieldDescriptor.CompareResult.Match)
                {
                    result.FieldsChanged.Add(field);
                }

                // why do we remove a field from current ones? if some field is still left after our operation, then field addition occured
                // we have to check that, cause it can be illegal from the version tolerance point of view
                prevFields.Remove(field.FullName);
            }

            // result should also contain transient fields, because some of them may
            // be marked with the [Constructor] attribute
            foreach(var nonTransient in prevFields.Values.Where(x => !x.IsTransient))
            {
                result.FieldsRemoved.Add(nonTransient);
            }

            return result;
        }

        public Type UnderlyingType { get; private set; }

        public string GenericFullName { get; private set; }
      
        public string GenericAssemblyQualifiedName { get; private set; }
      
        public AssemblyDescriptor TypeAssembly { get; private set; }

        public IEnumerable<FieldInfoOrEntryToOmit> FieldsToDeserialize { get; private set; }

        private TypeDescriptor()
        {
            fields = new List<FieldDescriptor>();
        }

        private void Init(Type t)
        {
            UnderlyingType = t;

            TypeAssembly = AssemblyDescriptor.CreateFromAssembly(t.Assembly);

            genericArguments = new List<TypeDescriptor>();
            if(UnderlyingType.IsGenericType)
            {
                GenericFullName = UnderlyingType.GetGenericTypeDefinition().FullName;
                GenericAssemblyQualifiedName = UnderlyingType.GetGenericTypeDefinition().AssemblyQualifiedName;
                foreach(var genericArgument in UnderlyingType.GetGenericArguments())
                {
                    genericArguments.Add(TypeDescriptor.CreateFromType(genericArgument));
                }
            }
            else
            {
                GenericAssemblyQualifiedName = UnderlyingType.AssemblyQualifiedName;
                GenericFullName = UnderlyingType.FullName;
            }

            if(t.BaseType != null)
            {
                System.IO.File.AppendAllText("/tmp/log", string.Format("Setting base type '{0}' for '{1}' in Init method\n", t.BaseType.FullName, t.FullName));
                baseType = TypeDescriptor.CreateFromType(t.BaseType);
            }

            var fieldsToDeserialize = new List<FieldInfoOrEntryToOmit>();
            foreach(var field in StampHelpers.GetFieldsInSerializationOrder(UnderlyingType, true))
            {
                fieldsToDeserialize.Add(new FieldInfoOrEntryToOmit(field));
                if(!field.IsTransient())
                {
                    fields.Add(new FieldDescriptor(field));
                }
            }
            FieldsToDeserialize = fieldsToDeserialize;
        }

        private void Resolve()
        {
            var type = TypeAssembly.UnderlyingAssembly.GetType(GenericFullName);
            if(type == null)
            {
                throw new InvalidOperationException(string.Format("Couldn't load type '{0}'", GenericFullName));
            }

            GenericAssemblyQualifiedName = type.AssemblyQualifiedName;

            if(type.IsGenericType)
            {
                var genericTypes = new Type[genericArguments.Count];
                var counter = 0;
                foreach(var genArg in genericArguments)
                {
                    genericTypes[counter++] = genArg.UnderlyingType;
                }
                type = type.MakeGenericType(genericTypes);
            }

            UnderlyingType = type;
        }

        private IEnumerable<FieldInfoOrEntryToOmit> GetConstructorRecreatedFields()
        {
            return FieldsToDeserialize.Where(x => x.Field != null && x.Field.IsConstructor());
        }

        private List<FieldInfoOrEntryToOmit> VerifyStructure(VersionToleranceLevel versionToleranceLevel)
        {
            if(TypeAssembly.ModuleGUID == UnderlyingType.Module.ModuleVersionId)
            {
                return StampHelpers.GetFieldsInSerializationOrder(UnderlyingType, true).Select(x => new FieldInfoOrEntryToOmit(x)).ToList();
            }

            if(!versionToleranceLevel.HasFlag(VersionToleranceLevel.AllowGuidChange))
            {
                throw new InvalidOperationException(string.Format("The class was serialized with different module version id {0}, current one is {1}.",
                    TypeAssembly.ModuleGUID, UnderlyingType.Module.ModuleVersionId));
            }

            var result = new List<FieldInfoOrEntryToOmit>();

            var assemblyTypeDescriptor = TypeDescriptor.CreateFromType(UnderlyingType);
            if( !(assemblyTypeDescriptor.baseType == null && baseType == null)
                && ((assemblyTypeDescriptor.baseType == null && baseType != null) || !assemblyTypeDescriptor.baseType.Equals(baseType)) 
                && !versionToleranceLevel.HasFlag(VersionToleranceLevel.AllowInheritanceChainChange))
            {
                throw new InvalidOperationException(string.Format("Class hierarchy changed. Expected '{1}' as base class, but found '{0}'.", baseType != null ? baseType.UnderlyingType.FullName : "null", assemblyTypeDescriptor.baseType != null ? assemblyTypeDescriptor.baseType.UnderlyingType.FullName : "null"));
            }

            if(assemblyTypeDescriptor.TypeAssembly.Version != TypeAssembly.Version && !versionToleranceLevel.HasFlag(VersionToleranceLevel.AllowAssemblyVersionChange))
            {
                throw new InvalidOperationException(string.Format("Assembly version changed from {0} to {1} for class {2}", TypeAssembly.Version, assemblyTypeDescriptor.TypeAssembly.Version, UnderlyingType.FullName));
            }

            var cmpResult = assemblyTypeDescriptor.CompareWith(this, versionToleranceLevel);

            if(cmpResult.FieldsChanged.Any())
            {
                throw new InvalidOperationException(string.Format("Field {0} type changed.", cmpResult.FieldsChanged[0].Name));
            }

            if(cmpResult.FieldsAdded.Any() && !versionToleranceLevel.HasFlag(VersionToleranceLevel.AllowFieldAddition))
            {
                throw new InvalidOperationException(string.Format("Field added: {0}.", cmpResult.FieldsAdded[0].Name));
            }
            if(cmpResult.FieldsRemoved.Any() && !versionToleranceLevel.HasFlag(VersionToleranceLevel.AllowFieldRemoval))
            {
                throw new InvalidOperationException(string.Format("Field removed: {0}.", cmpResult.FieldsRemoved[0].Name));
            }

            foreach(var field in fields)
            {
                if(cmpResult.FieldsRemoved.Contains(field))
                {
                    result.Add(new FieldInfoOrEntryToOmit(field.FieldType.UnderlyingType));
                }
                else
                {
                    result.Add(new FieldInfoOrEntryToOmit(field.UnderlyingFieldInfo));
                }
            }

            foreach(var field in assemblyTypeDescriptor.GetConstructorRecreatedFields().Select(x => x.Field))
            {
                result.Add(new FieldInfoOrEntryToOmit(field));
            }

            return result;
        }

        private void ReadTypeStamp(ObjectReader reader)
        {
            TypeAssembly = reader.ReadAssembly();
            GenericFullName = reader.PrimitiveReader.ReadString();
            var genericArgumentsCount = reader.PrimitiveReader.ReadInt32();
            genericArguments = new List<TypeDescriptor>();
            for(int i = 0; i < genericArgumentsCount; i++)
            {
                genericArguments.Add(reader.ReadType());
            }
            Resolve();
        }

        private void ReadStructureStamp(ObjectReader reader, VersionToleranceLevel versionToleranceLevel)
        {
            baseType = reader.ReadType();
            var noOfFields = reader.PrimitiveReader.ReadInt32();
            for(int i = 0; i < noOfFields; i++)
            {
                var fieldDescriptor = new FieldDescriptor(this);
                fieldDescriptor.ReadFrom(reader);
                fields.Add(fieldDescriptor);
            }
            FieldsToDeserialize = VerifyStructure(versionToleranceLevel);
            cache[UnderlyingType] = this;
        }

        private void WriteStructureStamp(ObjectWriter writer)
        {
            if(baseType == null)
            {
                writer.PrimitiveWriter.Write(Consts.NullObjectId);
            }
            else
            {
                writer.TouchAndWriteTypeId(baseType.UnderlyingType);
            }

            writer.PrimitiveWriter.Write(fields.Count);
            foreach(var field in fields)
            {
                field.WriteTo(writer);
            }
        }

        private List<TypeDescriptor> genericArguments;

        private TypeDescriptor baseType;

        private readonly List<FieldDescriptor> fields;

        private static ConcurrentDictionary<Type, TypeDescriptor> cache = new ConcurrentDictionary<Type, TypeDescriptor>();

        public class TypeDescriptorCompareResult
        {
            public List<FieldDescriptor> FieldsRemoved { get; private set; }

            public List<FieldDescriptor> FieldsAdded { get; private set; }

            public List<FieldDescriptor> FieldsChanged { get; private set; }

            public bool Empty { get { return FieldsRemoved.Count == 0 && FieldsAdded.Count == 0 && FieldsChanged.Count == 0; } }

            public TypeDescriptorCompareResult()
            {
                FieldsRemoved = new List<FieldDescriptor>();
                FieldsAdded = new List<FieldDescriptor>();
                FieldsChanged = new List<FieldDescriptor>();
            }
        }
    }
}

