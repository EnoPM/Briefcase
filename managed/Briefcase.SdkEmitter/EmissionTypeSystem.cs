using System.Reflection;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.SdkEmitter;

/// <summary>
/// Centralizes the executable types used by Reflection.Emit. Keeping them in
/// one type universe is required for constructed generics such as
/// IUnrealObject&lt;TGenerated&gt; and for Span-based interface methods.
/// </summary>
internal sealed class EmissionTypeSystem : IDisposable
{
    public Assembly CoreAssembly => typeof(object).Assembly;
    public Type Object => typeof(object);
    public Type ValueType => typeof(ValueType);
    public Type Void => typeof(void);
    public Type Int32 => typeof(int);
    public Type Single => typeof(float);
    public Type Boolean => typeof(bool);
    public Type String => typeof(string);
    public Type Type => typeof(Type);
    public Type Byte => typeof(byte);
    public Type SByte => typeof(sbyte);
    public Type Int16 => typeof(short);
    public Type UInt16 => typeof(ushort);
    public Type UInt32 => typeof(uint);
    public Type Int64 => typeof(long);
    public Type UInt64 => typeof(ulong);
    public Type Double => typeof(double);
    public Type SpanByte => typeof(Span<byte>);
    public Type ArgumentException => typeof(ArgumentException);
    public Type MemoryMarshal => typeof(System.Runtime.InteropServices.MemoryMarshal);
    public Type UnrealApi => typeof(UnrealApi);
    public Type UnrealObject => typeof(UnrealObject);
    public Type UnrealObjectHandle => typeof(UnrealObjectHandle);
    public Type UnrealObjectReference => typeof(UnrealObjectReference);
    public Type UnrealName => typeof(UnrealName);
    public Type UnrealText => typeof(UnrealText);
    public Type UnrealFunction => typeof(UnrealFunction);
    public Type UnrealParameter => typeof(UnrealParameter);
    public Type IUnrealStructValue => typeof(IUnrealStructValue);
    public Type IUnrealObject => typeof(IUnrealObject<>);
    public Type UnrealClass => typeof(UnrealClass<>);
    public Type UnrealProperty => typeof(UnrealProperty<>);
    public Type UnrealArray => typeof(UnrealArray<>);
    public Type UnrealSet => typeof(UnrealSet<>);
    public Type UnrealMap => typeof(UnrealMap<,>);
    public Type UnrealInterfaceReference => typeof(UnrealInterfaceReference);
    public Type UnrealLazyObjectReference => typeof(UnrealLazyObjectReference);
    public Type UnrealSoftObjectReference => typeof(UnrealSoftObjectReference);
    public Type UnrealSoftClassReference => typeof(UnrealSoftClassReference);
    public Type UnrealDelegate => typeof(UnrealDelegate);
    public Type UnrealMulticastDelegate => typeof(UnrealMulticastDelegate);
    public Type UnrealFieldPath => typeof(UnrealFieldPath);
    public Type UnrealInvocationResult => typeof(UnrealInvocationResult);
    public Type UnrealReflectedType => typeof(UnrealReflectedType);
    public Type UnrealReflectedProperty => typeof(UnrealReflectedProperty);
    public Type UnrealReflectedFunction => typeof(UnrealReflectedFunction);
    public Type UnrealReflectedParameter => typeof(UnrealReflectedParameter);
    public Type UnrealTypeMetadata => typeof(UnrealTypeMetadata);
    public Type UnrealBooleanLayout => typeof(UnrealBooleanLayout);
    public Type UnrealTypePathAttribute => typeof(UnrealTypePathAttribute);
    public Type Nullable => typeof(Nullable<>);
    public Type ByteArray => typeof(byte[]);

    public Type Resolve(
        ManagedTypeDescriptor descriptor,
        IReadOnlyDictionary<string, System.Reflection.Emit.TypeBuilder> generatedTypes) =>
        descriptor.Kind switch
        {
            ManagedTypeKind.Boolean => Boolean,
            ManagedTypeKind.Int8 => SByte,
            ManagedTypeKind.UInt8 => Byte,
            ManagedTypeKind.Int16 => Int16,
            ManagedTypeKind.UInt16 => UInt16,
            ManagedTypeKind.Int32 => Int32,
            ManagedTypeKind.UInt32 => UInt32,
            ManagedTypeKind.Int64 => Int64,
            ManagedTypeKind.UInt64 => UInt64,
            ManagedTypeKind.Float => Single,
            ManagedTypeKind.Double => Double,
            ManagedTypeKind.ObjectReference => UnrealObjectReference,
            ManagedTypeKind.String => String,
            ManagedTypeKind.Text => UnrealText,
            ManagedTypeKind.Name => UnrealName,
            ManagedTypeKind.ByteArray => ByteArray,
            ManagedTypeKind.Array when descriptor.InnerType is { } inner =>
                UnrealArray.MakeGenericType(Resolve(inner, generatedTypes)),
            ManagedTypeKind.Set when descriptor.InnerType is { } element =>
                UnrealSet.MakeGenericType(Resolve(element, generatedTypes)),
            ManagedTypeKind.Map when descriptor.KeyType is { } key &&
                                     descriptor.ValueType is { } value =>
                UnrealMap.MakeGenericType(
                    Resolve(key, generatedTypes), Resolve(value, generatedTypes)),
            ManagedTypeKind.InterfaceReference => UnrealInterfaceReference,
            ManagedTypeKind.LazyObjectReference => UnrealLazyObjectReference,
            ManagedTypeKind.SoftObjectReference => UnrealSoftObjectReference,
            ManagedTypeKind.SoftClassReference => UnrealSoftClassReference,
            ManagedTypeKind.Delegate => UnrealDelegate,
            ManagedTypeKind.MulticastDelegate => UnrealMulticastDelegate,
            ManagedTypeKind.FieldPath => UnrealFieldPath,
            ManagedTypeKind.GeneratedStruct when descriptor.ReferencedTypePath is { } path =>
                generatedTypes[path],
            _ => throw new InvalidDataException($"Unsupported managed type {descriptor}.")
        };

    public void Dispose() { }
}
