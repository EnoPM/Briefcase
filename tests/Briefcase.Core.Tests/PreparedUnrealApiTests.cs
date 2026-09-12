using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using Briefcase.SdkEmitter;
using Briefcase.SdkSnapshots;

namespace Briefcase.Core.Tests;

public sealed unsafe class PreparedUnrealApiTests
{
    private static int canonicalCaptureEnabled;
    private static int preparedDescriptorKind;
    private static int preparedDescriptorFlags;
    private static uint canonicalInputIndex;
    private static uint canonicalInputSerial;
    private static int valueV2CaptureEnabled;
    private static int valueV2InputValid;
    private static int valueV2PropertyValid;

    [Fact]
    public void Prepared_invocation_has_zero_steady_state_managed_allocations()
    {
        var native = CreateApi();
        var api = new UnrealApi(&native);
        var function = new UnrealFunction(
            "/Script/DeceiveInc.Controller", "TickPrepared", 0, []);
        var handle = new UnrealObjectHandle(1, 1);
        long preparedToken = 0;

        api.InvokeGenerated(handle, function, ref preparedToken, 0, 0);
        api.InvokeVoid(handle, function, Array.Empty<object?>());

        const int iterations = 1_000;
        var preparedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
            api.InvokeGenerated(handle, function, ref preparedToken, 0, 0);
        var preparedBytes = GC.GetAllocatedBytesForCurrentThread() - preparedBefore;

        var dynamicBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
            api.InvokeVoid(handle, function, Array.Empty<object?>());
        var dynamicBytes = GC.GetAllocatedBytesForCurrentThread() - dynamicBefore;

        Assert.Equal(0, preparedBytes);
        Assert.True(dynamicBytes > 0,
            $"The dynamic baseline unexpectedly allocated only {dynamicBytes} bytes.");
    }

    [Fact]
    public void Generated_canonical_path_round_trips_a_nested_object_handle()
    {
        const ulong outParameter = 0x100;
        const ulong referenceParameter = 0x08000000;
        var snapshot = new SdkSnapshot
        {
            SchemaVersion = 3,
            Target = "Client",
            SdkAssemblyName = "Briefcase.DeceiveInc.CanonicalJit"
        };
        snapshot.Types.AddRange([
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.HandlePayload",
                Name = "HandlePayload",
                Kind = "ScriptStruct",
                Size = 8,
                Properties = [Property("Object", "ObjectProperty", 0, 8)]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Controller",
                Name = "Controller",
                Kind = "Class",
                Properties =
                [
                    new PropertySnapshot
                    {
                        Name = "Payload",
                        UnrealType = "StructProperty",
                        Offset = 32,
                        ElementSize = 8,
                        ArrayDimension = 1,
                        Type = new UnrealTypeSnapshot
                        {
                            UnrealType = "StructProperty",
                            ElementSize = 8,
                            ReferencedTypePath = "/Script/DeceiveInc.HandlePayload"
                        }
                    }
                ],
                Functions =
                [
                    new FunctionSnapshot
                    {
                        Name = "Mutate",
                        ParameterSize = 8,
                        Parameters =
                        [
                            new PropertySnapshot
                            {
                                Name = "Payload",
                                UnrealType = "StructProperty",
                                Offset = 0,
                                ElementSize = 8,
                                ArrayDimension = 1,
                                Flags = outParameter | referenceParameter,
                                Type = new UnrealTypeSnapshot
                                {
                                    UnrealType = "StructProperty",
                                    ElementSize = 8,
                                    ReferencedTypePath = "/Script/DeceiveInc.HandlePayload"
                                }
                            }
                        ]
                    }
                ]
            }
        ]);

        var directory = Path.Combine(Path.GetTempPath(), "BriefcaseCanonicalJit-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var assemblyPath = Path.Combine(directory, "CanonicalJit.dll");
            SnapshotSdkEmitter.Emit(snapshot, assemblyPath, snapshot.SdkAssemblyName);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
            var payloadType = assembly.GetType("Briefcase.DeceiveInc.FHandlePayload", true)!;
            var controllerType = assembly.GetType("Briefcase.DeceiveInc.Controller", true)!;
            var native = CreateApi();
            var api = new UnrealApi(&native);
            var instance = controllerType.GetMethod("FromObject")!.Invoke(
                null, [api, new UnrealObjectHandle(1, 1)]);
            var payload = Activator.CreateInstance(payloadType)!;
            payloadType.GetField("Object")!.SetValue(
                payload, new UnrealObjectReference(new UnrealObjectHandle(7, 2)));
            object?[] arguments = [payload];

            Volatile.Write(ref canonicalCaptureEnabled, 1);
            controllerType.GetMethod("Mutate")!.Invoke(instance, arguments);
            var output = arguments[0]!;
            var outputReference = Assert.IsType<UnrealObjectReference>(
                payloadType.GetField("Object")!.GetValue(output));

            Assert.Equal((int)UnrealPropertyKind.Struct, Volatile.Read(ref preparedDescriptorKind));
            Assert.Equal(
                (int)(NativePreparedParameterFlags.Input |
                      NativePreparedParameterFlags.Output |
                      NativePreparedParameterFlags.Reference),
                Volatile.Read(ref preparedDescriptorFlags));
            Assert.Equal(7u, Volatile.Read(ref canonicalInputIndex));
            Assert.Equal(2u, Volatile.Read(ref canonicalInputSerial));
            Assert.Equal(new UnrealObjectHandle(9, 3), outputReference.Handle);

            var generatedProperty = controllerType.GetProperty("Payload")!;
            var read = generatedProperty.GetValue(instance)!;
            var readReference = Assert.IsType<UnrealObjectReference>(
                payloadType.GetField("Object")!.GetValue(read));
            Assert.Equal(new UnrealObjectHandle(9, 3), readReference.Handle);
            generatedProperty.SetValue(instance, payload);
            Assert.Equal(7u, Volatile.Read(ref canonicalInputIndex));
            Assert.Equal(2u, Volatile.Read(ref canonicalInputSerial));
        }
        finally
        {
            Volatile.Write(ref canonicalCaptureEnabled, 0);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Generated_fast_path_is_valid_JIT_IL_and_returns_native_outputs()
    {
        const ulong returnParameter = 0x400;
        var snapshot = new SdkSnapshot
        {
            SchemaVersion = 3,
            Target = "Client",
            SdkAssemblyName = "Briefcase.DeceiveInc.FastJit"
        };
        snapshot.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.Controller",
            Name = "Controller",
            Kind = "Class",
            Functions =
            [
                new FunctionSnapshot
                {
                    Name = "SetMode",
                    ParameterSize = 8,
                    Parameters =
                    [
                        Property("Mode", "IntProperty", 0, 4),
                        Property("ReturnValue", "BoolProperty", 4, 1, returnParameter)
                    ]
                }
            ]
        });
        var directory = Path.Combine(Path.GetTempPath(), "BriefcaseFastJit-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var assemblyPath = Path.Combine(directory, "FastJit.dll");
            SnapshotSdkEmitter.Emit(snapshot, assemblyPath, snapshot.SdkAssemblyName);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
            var controller = assembly.GetType("Briefcase.DeceiveInc.Controller", true)!;
            var native = CreateApi();
            var api = new UnrealApi(&native);
            var instance = controller.GetMethod("FromObject")!.Invoke(
                null, [api, new UnrealObjectHandle(1, 1)]);

            var result = controller.GetMethod("SetMode")!.Invoke(instance, [3]);

            Assert.True(Assert.IsType<bool>(result));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Generated_owning_input_ref_and_property_write_use_BVC1_v13()
    {
        const ulong outParameter = 0x100;
        const ulong referenceParameter = 0x08000000;
        const string payloadPath = "/Script/DeceiveInc.TextPayload";
        var snapshot = new SdkSnapshot
        {
            SchemaVersion = 3,
            Target = "Client",
            SdkAssemblyName = "Briefcase.DeceiveInc.ValueV2Jit"
        };
        snapshot.Types.AddRange([
            new TypeSnapshot
            {
                Path = payloadPath,
                Name = "TextPayload",
                Kind = "ScriptStruct",
                Size = 16,
                Properties = [Property("Label", "StrProperty", 0, 16)]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Controller",
                Name = "Controller",
                Kind = "Class",
                Properties =
                [
                    new PropertySnapshot
                    {
                        Name = "Payload", UnrealType = "StructProperty", Offset = 32,
                        ElementSize = 16, ArrayDimension = 1,
                        Type = new UnrealTypeSnapshot
                        {
                            UnrealType = "StructProperty", ElementSize = 16,
                            ReferencedTypePath = payloadPath
                        }
                    }
                ],
                Functions =
                [
                    new FunctionSnapshot
                    {
                        Name = "Update", ParameterSize = 16,
                        Parameters =
                        [
                            new PropertySnapshot
                            {
                                Name = "Payload", UnrealType = "StructProperty", Offset = 0,
                                ElementSize = 16, ArrayDimension = 1,
                                Flags = outParameter | referenceParameter,
                                Type = new UnrealTypeSnapshot
                                {
                                    UnrealType = "StructProperty", ElementSize = 16,
                                    ReferencedTypePath = payloadPath
                                }
                            }
                        ]
                    }
                ]
            }
        ]);

        var directory = Path.Combine(Path.GetTempPath(), "BriefcaseValueV2Jit-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var assemblyPath = Path.Combine(directory, "ValueV2Jit.dll");
            SnapshotSdkEmitter.Emit(snapshot, assemblyPath, snapshot.SdkAssemblyName);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
            var payloadType = assembly.GetType("Briefcase.DeceiveInc.FTextPayload", true)!;
            var controllerType = assembly.GetType("Briefcase.DeceiveInc.Controller", true)!;
            var payload = Activator.CreateInstance(payloadType)!;
            payloadType.GetField("Label")!.SetValue(payload, "hello");
            var native = CreateApi();
            var api = new UnrealApi(&native);
            var instance = controllerType.GetMethod("FromObject")!.Invoke(
                null, [api, new UnrealObjectHandle(1, 1)]);

            Volatile.Write(ref valueV2CaptureEnabled, 1);
            object?[] arguments = [payload];
            controllerType.GetMethod("Update")!.Invoke(instance, arguments);
            controllerType.GetProperty("Payload")!.SetValue(instance, payload);

            Assert.Equal("hello", payloadType.GetField("Label")!.GetValue(arguments[0]));
            Assert.Equal(1, Volatile.Read(ref valueV2InputValid));
            Assert.Equal(1, Volatile.Read(ref valueV2PropertyValid));
        }
        finally
        {
            Volatile.Write(ref valueV2CaptureEnabled, 0);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Generated_container_property_and_ref_input_use_BVC1_v13()
    {
        const ulong outParameter = 0x100;
        const ulong referenceParameter = 0x08000000;
        static UnrealTypeSnapshot ArrayOfInt() => new()
        {
            UnrealType = "ArrayProperty",
            ElementSize = 16,
            InnerType = new UnrealTypeSnapshot
            {
                UnrealType = "IntProperty",
                ElementSize = 4
            }
        };

        var snapshot = new SdkSnapshot
        {
            SchemaVersion = 3,
            Target = "Client",
            SdkAssemblyName = "Briefcase.DeceiveInc.ContainerV3Jit",
            Types =
            [
                new TypeSnapshot
                {
                    Path = "/Script/DeceiveInc.Controller",
                    Name = "Controller",
                    Kind = "Class",
                    Properties =
                    [
                        new PropertySnapshot
                        {
                            Name = "Values", UnrealType = "ArrayProperty",
                            Offset = 32, ElementSize = 16, ArrayDimension = 1,
                            Type = ArrayOfInt()
                        }
                    ],
                    Functions =
                    [
                        new FunctionSnapshot
                        {
                            Name = "ReplaceValues", ParameterSize = 16,
                            Parameters =
                            [
                                new PropertySnapshot
                                {
                                    Name = "Values", UnrealType = "ArrayProperty",
                                    Offset = 0, ElementSize = 16, ArrayDimension = 1,
                                    Flags = outParameter | referenceParameter,
                                    Type = ArrayOfInt()
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var directory = Path.Combine(
            Path.GetTempPath(), "BriefcaseContainerV3Jit-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var assemblyPath = Path.Combine(directory, "ContainerV3Jit.dll");
            SnapshotSdkEmitter.Emit(snapshot, assemblyPath, snapshot.SdkAssemblyName);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
            var controllerType = assembly.GetType("Briefcase.DeceiveInc.Controller", true)!;
            var native = CreateApi();
            var api = new UnrealApi(&native);
            var instance = controllerType.GetMethod("FromObject")!.Invoke(
                null, [api, new UnrealObjectHandle(1, 1)]);
            var input = new UnrealArray<int>(3, 5, 8);

            Volatile.Write(ref valueV2CaptureEnabled, 1);
            controllerType.GetProperty("Values")!.SetValue(instance, input);
            object?[] arguments = [input];
            controllerType.GetMethod("ReplaceValues")!.Invoke(instance, arguments);

            Assert.Equal([3, 5, 8], Assert.IsType<UnrealArray<int>>(arguments[0]).ToArray());
            Assert.Equal(1, Volatile.Read(ref valueV2InputValid));
            Assert.Equal(1, Volatile.Read(ref valueV2PropertyValid));
        }
        finally
        {
            Volatile.Write(ref valueV2CaptureEnabled, 0);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Generated_FString_and_FText_refs_use_owned_BVC1_values()
    {
        const ulong outParameter = 0x100;
        const ulong referenceParameter = 0x08000000;
        var snapshot = new SdkSnapshot
        {
            SchemaVersion = 3,
            Target = "Client",
            SdkAssemblyName = "Briefcase.DeceiveInc.OwningTextJit",
            Types =
            [
                new TypeSnapshot
                {
                    Path = "/Script/DeceiveInc.TextController",
                    Name = "TextController",
                    Kind = "Class",
                    Functions =
                    [
                        new FunctionSnapshot
                        {
                            Name = "RewriteName", ParameterSize = 16,
                            Parameters = [Property(
                                "Value", "StrProperty", 0, 16,
                                outParameter | referenceParameter)]
                        },
                        new FunctionSnapshot
                        {
                            Name = "RewriteLabel", ParameterSize = 24,
                            Parameters = [Property(
                                "Value", "TextProperty", 0, 24,
                                outParameter | referenceParameter)]
                        }
                    ]
                }
            ]
        };

        var directory = Path.Combine(
            Path.GetTempPath(), "BriefcaseOwningTextJit-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var assemblyPath = Path.Combine(directory, "OwningTextJit.dll");
            SnapshotSdkEmitter.Emit(snapshot, assemblyPath, snapshot.SdkAssemblyName);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
            var controllerType = assembly.GetType(
                "Briefcase.DeceiveInc.TextController", true)!;
            var native = CreateApi();
            var api = new UnrealApi(&native);
            var instance = controllerType.GetMethod("FromObject")!.Invoke(
                null, [api, new UnrealObjectHandle(1, 1)]);

            Volatile.Write(ref valueV2CaptureEnabled, 1);
            object?[] nameArguments = ["Agent Name"];
            controllerType.GetMethod("RewriteName")!.Invoke(instance, nameArguments);
            Assert.Equal("Agent Name", nameArguments[0]);

            object?[] labelArguments = [new UnrealText("Localized label")];
            controllerType.GetMethod("RewriteLabel")!.Invoke(instance, labelArguments);
            Assert.Equal(
                new UnrealText("Localized label"),
                Assert.IsType<UnrealText>(labelArguments[0]));
            Assert.Equal(1, Volatile.Read(ref valueV2InputValid));
        }
        finally
        {
            Volatile.Write(ref valueV2CaptureEnabled, 0);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Generated_FString_return_and_out_decode_owned_BVO1_values()
    {
        const ulong outParameter = 0x100;
        const ulong returnParameter = 0x400;
        var snapshot = new SdkSnapshot
        {
            SchemaVersion = 3,
            Target = "Client",
            SdkAssemblyName = "Briefcase.DeceiveInc.OwningTextOutputJit",
            Types =
            [
                new TypeSnapshot
                {
                    Path = "/Script/DeceiveInc.TextController",
                    Name = "TextController",
                    Kind = "Class",
                    Functions =
                    [
                        new FunctionSnapshot
                        {
                            Name = "GetName", ParameterSize = 16,
                            Parameters = [Property(
                                "ReturnValue", "StrProperty", 0, 16,
                                returnParameter)]
                        },
                        new FunctionSnapshot
                        {
                            Name = "GetNameOut", ParameterSize = 16,
                            Parameters = [Property(
                                "Value", "StrProperty", 0, 16,
                                outParameter)]
                        }
                    ]
                }
            ]
        };

        var directory = Path.Combine(
            Path.GetTempPath(), "BriefcaseOwningTextOutputJit-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var assemblyPath = Path.Combine(directory, "OwningTextOutputJit.dll");
            SnapshotSdkEmitter.Emit(snapshot, assemblyPath, snapshot.SdkAssemblyName);
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
            var controllerType = assembly.GetType(
                "Briefcase.DeceiveInc.TextController", true)!;
            var native = CreateApi();
            var api = new UnrealApi(&native);
            var instance = controllerType.GetMethod("FromObject")!.Invoke(
                null, [api, new UnrealObjectHandle(1, 1)]);

            Volatile.Write(ref valueV2CaptureEnabled, 1);
            Assert.Equal(
                "Native result",
                controllerType.GetMethod("GetName")!.Invoke(instance, null));
            object?[] arguments = [null];
            controllerType.GetMethod("GetNameOut")!.Invoke(instance, arguments);
            Assert.Equal("Native result", arguments[0]);
        }
        finally
        {
            Volatile.Write(ref valueV2CaptureEnabled, 0);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PropertySnapshot Property(
        string name, string unrealType, int offset, int size, ulong flags = 0) => new()
        {
            Name = name,
            UnrealType = unrealType,
            Offset = offset,
            ElementSize = size,
            ArrayDimension = 1,
            Flags = flags
        };

    private static NativeUnrealApi CreateApi() => new()
    {
        StructSize = checked((uint)sizeof(NativeUnrealApi)),
        ApiVersion = BriefcaseAbi.UnrealApiVersion,
        FindObject = &FindObject,
        FindObjectsOfClass = &FindObjectsOfClass,
        GetObjectName = &GetObjectText,
        GetObjectPath = &GetObjectText,
        GetPropertyInfo = &GetPropertyInfo,
        ReadProperty = &ReadProperty,
        InvokeFunction = &InvokeFunction,
        PrepareFunction = &PrepareFunction,
        InvokePreparedFunction = &InvokePreparedFunction,
        InvokePreparedValueFunction = &InvokePreparedValueFunction,
        PrepareProperty = &PrepareProperty,
        ReadPreparedProperty = &ReadPreparedProperty,
        WritePreparedProperty = &WritePreparedProperty,
        InvokePreparedValueFunctionV2 = &InvokePreparedValueFunctionV2,
        ReleaseValueBuffer = &ReleaseValueBuffer,
        WritePreparedValueProperty = &WritePreparedValueProperty
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult FindObject(
        void* _, byte* __, uint ___, UnrealObjectHandle* result)
    {
        *result = new UnrealObjectHandle(2, 1);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult FindObjectsOfClass(
        void* _, UnrealObjectHandle __, UnrealObjectHandle* ___,
        uint ____, uint* written, uint* total)
    {
        *written = 0;
        *total = 0;
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetObjectText(
        void* _, UnrealObjectHandle __, byte* ___, uint ____, uint* required)
    {
        *required = 1;
        return NativeUnrealResult.BufferTooSmall;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetPropertyInfo(
        void* _, UnrealObjectHandle __, byte* ___, uint ____, NativePropertyInfo* _____) =>
        NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult ReadProperty(
        void* _, UnrealObjectHandle __, UnrealObjectHandle ___, byte* ____, uint _____,
        int ______, int _______, int ________, UnrealPropertyKind _________,
        void* __________, uint ___________) => NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult InvokeFunction(
        void* _, UnrealObjectHandle __, UnrealObjectHandle ___, byte* ____, uint _____,
        uint ______, void* _______, uint ________) => NativeUnrealResult.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult PrepareFunction(
        void* _, UnrealObjectHandle __, byte* ___, uint ____, uint _____,
        NativePreparedParameter* parameters, uint parameterCount, ulong* token)
    {
        if (Volatile.Read(ref canonicalCaptureEnabled) != 0 && parameterCount == 1)
        {
            Volatile.Write(ref preparedDescriptorKind, (int)parameters[0].Kind);
            Volatile.Write(ref preparedDescriptorFlags, (int)parameters[0].Flags);
        }
        *token = 42;
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult InvokePreparedFunction(
        void* _, ulong __, UnrealObjectHandle ___, void* parameters, uint parameterSize)
    {
        if (parameters != null && parameterSize >= 8 &&
            Volatile.Read(ref canonicalCaptureEnabled) != 0)
        {
            var input = *(UnrealObjectHandle*)parameters;
            Volatile.Write(ref canonicalInputIndex, input.Index);
            Volatile.Write(ref canonicalInputSerial, input.SerialNumber);
            *(UnrealObjectHandle*)parameters = new UnrealObjectHandle(9, 3);
        }
        else if (parameters != null && parameterSize >= 5)
        {
            ((byte*)parameters)[4] = 1;
        }
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult PrepareProperty(
        void* _, UnrealObjectHandle __, byte* ___, uint ____, int _____, int ______,
        int _______, UnrealPropertyKind ________, ulong* token)
    {
        *token = 43;
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult ReadPreparedProperty(
        void* _, ulong __, UnrealObjectHandle ___, void* output, uint outputSize)
    {
        if (Volatile.Read(ref canonicalCaptureEnabled) != 0 &&
            output != null && outputSize >= 8)
            *(UnrealObjectHandle*)output = new UnrealObjectHandle(9, 3);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult WritePreparedProperty(
        void* _, ulong __, UnrealObjectHandle ___, void* input, uint inputSize)
    {
        if (Volatile.Read(ref canonicalCaptureEnabled) != 0 &&
            input != null && inputSize >= 8)
        {
            var value = *(UnrealObjectHandle*)input;
            Volatile.Write(ref canonicalInputIndex, value.Index);
            Volatile.Write(ref canonicalInputSerial, value.SerialNumber);
        }
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult InvokePreparedValueFunctionV2(
        void* _, ulong __, UnrealObjectHandle ___, void* ____, uint _____,
        NativeValueInput* inputs, uint inputCount, NativeOwnedValueBuffer* output)
    {
        if (Volatile.Read(ref valueV2CaptureEnabled) == 0 || inputCount != 1 ||
            inputs == null || output == null || inputs[0].Data == null || inputs[0].Size < 12)
            return NativeUnrealResult.InvalidArgument;
        var input = new ReadOnlySpan<byte>(inputs[0].Data, checked((int)inputs[0].Size));
        if (BinaryPrimitives.ReadUInt32LittleEndian(input) != 0x31435642)
            return NativeUnrealResult.LayoutMismatch;
        Volatile.Write(ref valueV2InputValid, 1);

        var outputSize = checked((int)inputs[0].Size + 8);
        var allocation = (byte*)NativeMemory.Alloc((nuint)outputSize);
        if (allocation == null) return NativeUnrealResult.Unreadable;
        var bytes = new Span<byte>(allocation, outputSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x314f5642);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], inputs[0].ParameterOffset);
        input[4..].CopyTo(bytes[12..]);
        output->Data = allocation;
        output->Size = checked((uint)outputSize);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult InvokePreparedValueFunction(
        void* _, ulong __, UnrealObjectHandle ___, void* ____, uint _____,
        NativeOwnedValueBuffer* output)
    {
        if (Volatile.Read(ref valueV2CaptureEnabled) == 0 || output == null)
            return NativeUnrealResult.InvalidArgument;
        var encoded = UnrealValueWire.Encode(typeof(string), "Native result");
        var outputSize = checked(encoded.Length + 8);
        var allocation = (byte*)NativeMemory.Alloc((nuint)outputSize);
        if (allocation == null) return NativeUnrealResult.Unreadable;
        var bytes = new Span<byte>(allocation, outputSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x314f5642);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], 0);
        encoded.AsSpan(4).CopyTo(bytes[12..]);
        output->Data = allocation;
        output->Size = checked((uint)outputSize);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseValueBuffer(void* _, NativeOwnedValueBuffer* buffer)
    {
        if (buffer == null) return;
        NativeMemory.Free(buffer->Data);
        *buffer = default;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult WritePreparedValueProperty(
        void* _, ulong __, UnrealObjectHandle ___, byte* input, uint inputSize)
    {
        if (Volatile.Read(ref valueV2CaptureEnabled) == 0 || input == null || inputSize < 12)
            return NativeUnrealResult.InvalidArgument;
        var bytes = new ReadOnlySpan<byte>(input, checked((int)inputSize));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x31435642)
            return NativeUnrealResult.LayoutMismatch;
        Volatile.Write(ref valueV2PropertyValid, 1);
        return NativeUnrealResult.Ok;
    }
}
