﻿// ---------------------------------------------------------------------------------------
//                                        ILGPU
//                        Copyright (c) 2018-2023 ILGPU Project
//                                    www.ilgpu.net
//
// File: Disassembler.cs
//
// This file is part of ILGPU and is distributed under the University of Illinois Open
// Source License. See LICENSE.txt for details.
// ---------------------------------------------------------------------------------------

using ILGPU.Frontend.DebugInformation;
using ILGPU.IR;
using ILGPU.IR.Construction;
using ILGPU.Resources;
using ILGPU.Util;
using System;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace ILGPU.Frontend
{
    /// <summary>
    /// Represents a disassembler for .Net methods.
    /// </summary>
    /// <remarks>Members of this class are not thread safe.</remarks>
    public sealed partial class Disassembler : ILocation
    {
        #region Constants

        /// <summary>
        /// Represents the native pointer type that is used during the
        /// disassembling process.
        /// </summary>
        public static readonly Type NativePtrType = typeof(void).MakePointerType();

        #endregion

        #region Instance

        /// <summary>
        /// The current IL byte code.
        /// </summary>
        private readonly byte[] il;

        /// <summary>
        /// The current offset within the byte code.
        /// </summary>
        private int ilOffset;

        /// <summary>
        /// The current instruction type.
        /// </summary>
        private int instructionOffset;

        /// <summary>
        /// The current flags that are applied to the next instruction.
        /// </summary>
        private ILInstructionFlags flags;

        /// <summary>
        /// The current flags argument.
        /// </summary>
        private object? flagsArgument;

        /// <summary>
        /// Represents the current list of instructions.
        /// </summary>
        private readonly ImmutableArray<ILInstruction>.Builder instructions;

        /// <summary>
        /// Represents the associated sequence-point enumerator.
        /// </summary>
        private SequencePointEnumerator debugInformationEnumerator;

        /// <summary>
        /// Returns the source location.
        /// </summary>
        private readonly CompilationStackLocation? compilationStackLocation;

        /// <summary>
        /// Constructs a new disassembler.
        /// </summary>
        /// <param name="method">The target method.</param>
        /// <param name="sequencePointEnumerator">
        /// The associated sequence-point enumerator.
        /// </param>
        /// <param name="compilationStackLocation">The source location (optional).</param>
        public Disassembler(
            SpecializedMethod? method,
            SequencePointEnumerator sequencePointEnumerator,
            CompilationStackLocation? compilationStackLocation = null) {
            Method = method
                ?? throw new ArgumentNullException(nameof(method));
            MethodGenericArguments = Method.Underlying is MethodInfo
                ? Method.Underlying.GetGenericArguments()
                : Array.Empty<Type>();
            TypeGenericArguments = Method.Underlying.DeclaringType.AsNotNull().GetGenericArguments();
            MethodBody = Method.Underlying.GetMethodBody()
                ?? throw new NotSupportedException(string.Format(
                    ErrorMessages.NativeMethodNotSupported,
                    Method.Underlying.Name));
            il = MethodBody.GetILAsByteArray() ?? Array.Empty<byte>();
            instructions = ImmutableArray.CreateBuilder<ILInstruction>(il.Length);
            debugInformationEnumerator = sequencePointEnumerator;
            this.compilationStackLocation = compilationStackLocation;
            CurrentLocation = new Method.MethodLocation(method.Value.Underlying);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Returns the current method base.
        /// </summary>
        public SpecializedMethod Method { get; }

        /// <summary>
        /// Returns the current method body.
        /// </summary>
        public MethodBody MethodBody { get; }

        /// <summary>
        /// Returns the declaring type of the method.
        /// </summary>
        public Type DeclaringType => Method.Underlying.DeclaringType.AsNotNull();

        /// <summary>
        /// Returns the associated managed module.
        /// </summary>
        public Module AssociatedModule => DeclaringType.Module;

        /// <summary>
        /// Returns the generic arguments of the method.
        /// </summary>
        internal Type[] MethodGenericArguments { get; }

        /// <summary>
        /// Returns the generic arguments of the declaring type.
        /// </summary>
        internal Type[] TypeGenericArguments { get; }

        /// <summary>
        /// Returns the current location.
        /// </summary>
        public Location CurrentLocation { get; private set; }

        #endregion

        #region ILocation

        /// <summary>
        /// Formats an error message to include the current sequence point.
        /// </summary>
        string ILocation.FormatErrorMessage(string message)
        {
            var location = compilationStackLocation != null
                ? compilationStackLocation.Append(CurrentLocation)
                : CurrentLocation;
            return location.FormatErrorMessage(message);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Disassembles the current method and returns a list of
        /// disassembled instructions.
        /// </summary>
        /// <returns>The list of disassembled instructions.</returns>
        public DisassembledMethod Disassemble()
        {
            while (ilOffset < il.Length)
            {
                instructionOffset = ilOffset;
                var opCode = ReadOpCode();

                if (debugInformationEnumerator.MoveTo(instructionOffset) &&
                    debugInformationEnumerator.Current.IsKnown)
                {
                    CurrentLocation = debugInformationEnumerator.Current;
                }

                if (TryDisassemblePrefix(opCode))
                    continue;

                if (TryDisasembleInstruction(opCode))
                {
                    // Reset flags
                    flags = ILInstructionFlags.None;
                    flagsArgument = null;
                }
                else
                {
                    throw opCode switch
                    {
                        ILOpCode.Ldftn =>
                            this.GetNotSupportedException(
                                ErrorMessages.NotSupportedILInstructionPossibleLambda,
                                opCode),
                        _ => this.GetNotSupportedException(
                            ErrorMessages.NotSupportedILInstruction,
                            opCode)
                    };
                }
            }

            return new DisassembledMethod(
                Method,
                instructions.ToImmutable(),
                MethodBody.MaxStackSize);
        }

        /// <summary>
        /// Disassembles a call to the given method.
        /// </summary>
        /// <param name="type">The instruction type.</param>
        /// <param name="methodToken">The token of the method to be disassembled.</param>
        private void DisassembleCall(ILInstructionType type, int methodToken)
        {
            var method = ResolveMethod(methodToken).AsNotNull();
            var popCount = method.GetParameters().Length;
            var methodInfo = method as MethodInfo;
            int pushCount = 0;
            if (methodInfo != null)
            {
                popCount += method.GetParameterOffset();
                if (methodInfo.ReturnType != typeof(void))
                    pushCount = 1;
            }
            else if (method is ConstructorInfo)
            {
                if (type == ILInstructionType.Newobj)
                {
                    // We have to push the new object
                    pushCount += 1;
                }
                else
                {
                    // Check for an invalid constructor call
                    this.Assert(type == ILInstructionType.Call);
                    popCount += 1;
                }
            }
            AppendInstruction(type, (ushort)popCount, (ushort)pushCount, method);
        }

        /// <summary>
        /// Adds the given flags to the current instruction flags.
        /// </summary>
        /// <param name="flagsToAdd">The flags to be added.</param>
        private void AddFlags(ILInstructionFlags flagsToAdd) => flags |= flagsToAdd;

        /// <summary>
        /// Appends an instruction to the current instruction list.
        /// </summary>
        /// <param name="type">The instruction type.</param>
        /// <param name="popCount">
        /// The number of elements to pop from the stack.
        /// </param>
        /// <param name="pushCount">
        /// The number of elements to push onto the stack.
        /// </param>
        /// <param name="argument">The argument of the instruction.</param>
        private void AppendInstruction(
            ILInstructionType type,
            ushort popCount,
            ushort pushCount,
            object? argument = null) =>
            AppendInstructionWithFlags(
                type,
                popCount,
                pushCount,
                ILInstructionFlags.None,
                argument);

        /// <summary>
        /// Appends an instruction to the current instruction list.
        /// </summary>
        /// <param name="type">The instruction type.</param>
        /// <param name="popCount">
        /// The number of elements to pop from the stack.
        /// </param>
        /// <param name="pushCount">
        /// The number of elements to push onto the stack.
        /// </param>
        /// <param name="additionalFlags">Additional instruction flags.</param>
        /// <param name="argument">The argument of the instruction.</param>
        private void AppendInstructionWithFlags(
            ILInstructionType type,
            ushort popCount,
            ushort pushCount,
            ILInstructionFlags additionalFlags,
            object? argument = null) =>
            // Merge with current flags
            instructions.Add(new ILInstruction(
                instructionOffset,
                type,
                new ILInstructionFlagsContext(
                    additionalFlags | flags,
                    flagsArgument),
                popCount,
                pushCount,
                argument,
                CurrentLocation));

        #region Metadata

        /// <summary>
        /// Resolves the type for the given token using
        /// the current generic information.
        /// </summary>
        /// <param name="token">The token of the type to resolve.</param>
        /// <returns>The resolved type.</returns>
        private Type ResolveType(int token) =>
            AssociatedModule.ResolveType(
                token,
                TypeGenericArguments,
                MethodGenericArguments);

        /// <summary>
        /// Resolves the method for the given token using
        /// the current generic information.
        /// </summary>
        /// <param name="token">The token of the method to resolve.</param>
        /// <returns>The resolved method.</returns>
        private MethodBase? ResolveMethod(int token) =>
            AssociatedModule.ResolveMethod(
                token,
                TypeGenericArguments,
                MethodGenericArguments);

        /// <summary>
        /// Resolves the field for the given token using
        /// the current generic information.
        /// </summary>
        /// <param name="token">The token of the field to resolve.</param>
        /// <returns>The resolved field.</returns>
        private FieldInfo? ResolveField(int token) =>
            AssociatedModule.ResolveField(
                token,
                TypeGenericArguments,
                MethodGenericArguments);

        #endregion

        #region Read Methods

        /// <summary>
        /// Reads an op-code from the current instruction data.
        /// </summary>
        /// <returns>The decoded op-code.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ILOpCode ReadOpCode()
        {
            // Setup target block (if found)
            int instructionCode = il[ilOffset];
            if (instructionCode == OpCodes.Prefix1.Value)
            {
                // This is a two-byte command
                ++ilOffset;
                this.Assert(il.Length > ilOffset);
                instructionCode = (instructionCode << 8) | il[ilOffset];
            }
            else
            {
                // This is a single-byte command
            }
            ++ilOffset;
            return (ILOpCode)instructionCode;
        }

        /// <summary>
        /// Reads a short branch target from the current instruction data.
        /// </summary>
        /// <returns>The decoded short branch target.</returns>
        private int ReadShortBranchTarget() => ReadSByteArg() + ilOffset;

        /// <summary>
        /// Reads a branch target from the current instruction data.
        /// </summary>
        /// <returns>The decoded branch target.</returns>
        private int ReadBranchTarget() => ReadIntArg() + ilOffset;

        /// <summary>
        /// Reads a byte from the current instruction data.
        /// </summary>
        /// <returns>The decoded byte.</returns>
        private int ReadByteArg() => il[ilOffset++];

        /// <summary>
        /// Reads a type reference from the current instruction data.
        /// </summary>
        /// <returns>The decoded type reference.</returns>
        private Type ReadTypeArg()
        {
            var token = ReadIntArg();
            return ResolveType(token);
        }

        /// <summary>
        /// Reads a field reference from the current instruction data.
        /// </summary>
        /// <returns>The decoded field reference.</returns>
        private FieldInfo? ReadFieldArg()
        {
            var token = ReadIntArg();
            return ResolveField(token);
        }

        /// <summary>
        /// Reads a sbyte from the current instruction data.
        /// </summary>
        /// <returns>The decoded sbyte.</returns>
        private unsafe int ReadSByteArg()
        {
            fixed (byte* p = &il[ilOffset++])
            {
                return *(sbyte*)p;
            }
        }

        /// <summary>
        /// Reads an ushort from the current instruction data.
        /// </summary>
        /// <returns>The decoded ushort.</returns>
        private int ReadUShortArg()
        {
            var result = BitConverter.ToUInt16(il, ilOffset);
            ilOffset += sizeof(ushort);
            return result;
        }

        /// <summary>
        /// Reads an int from the current instruction data.
        /// </summary>
        /// <returns>The decoded int.</returns>
        private int ReadIntArg()
        {
            var result = BitConverter.ToInt32(il, ilOffset);
            ilOffset += sizeof(int);
            return result;
        }

        /// <summary>
        /// Reads an uint from the current instruction data.
        /// </summary>
        /// <returns>The decoded uint.</returns>
        private uint ReadUIntArg()
        {
            var result = BitConverter.ToUInt32(il, ilOffset);
            ilOffset += sizeof(uint);
            return result;
        }

        /// <summary>
        /// Reads a string from the current instruction data.
        /// </summary>
        /// <returns>The decoded string.</returns>
        private float ReadSingleArg()
        {
            var result = BitConverter.ToSingle(il, ilOffset);
            ilOffset += sizeof(float);
            return result;
        }

        /// <summary>
        /// Reads a long from the current instruction data.
        /// </summary>
        /// <returns>The decoded long.</returns>
        private long ReadLongArg()
        {
            var result = BitConverter.ToInt64(il, ilOffset);
            ilOffset += sizeof(long);
            return result;
        }

        /// <summary>
        /// Reads a double from the current instruction data.
        /// </summary>
        /// <returns>The decoded double.</returns>
        private double ReadDoubleArg()
        {
            var result = BitConverter.ToDouble(il, ilOffset);
            ilOffset += sizeof(double);
            return result;
        }

        #endregion

        #endregion
    }
}
