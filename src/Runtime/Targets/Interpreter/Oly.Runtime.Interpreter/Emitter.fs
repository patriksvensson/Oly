﻿namespace rec Oly.Runtime.Interpreter

open System
open System.IO
open System.Text
open System.Collections.Generic
open System.Collections.Concurrent
open System.Threading
open Oly.Core
open Oly.Core.TaskExtensions
open Oly.Metadata
open Oly.Runtime
open Oly.Runtime.CodeGen

[<Sealed>]
type InterpreterEnvironment() =

    let builder = StringBuilder()

    member _.StandardOut = builder

[<RequireQualifiedAccess;NoEquality;NoComparison>]
type InterpreterTypeDefinitionInfo =
    {
        mutable inherits: InterpreterType imarray
        mutable implements: InterpreterType imarray
        mutable runtimeTyOpt: InterpreterType option
        fields: ResizeArray<InterpreterField>
        funcs: ResizeArray<InterpreterFunction>
    }

    static member Default =
        {
            inherits = ImArray.empty
            implements = ImArray.empty
            runtimeTyOpt = None
            fields = ResizeArray()
            funcs = ResizeArray()
        }

[<RequireQualifiedAccess;NoEquality;NoComparison>]
type InterpreterType =
    | Void
    | Unit
    | Int8
    | UInt8
    | Int16
    | UInt16
    | Int32
    | UInt32
    | Int64
    | UInt64
    | Float32
    | Float64
    | Bool
    | Char16
    | Utf16

    | ByReference of InterpreterType
    | InReference of InterpreterType

    | Tuple of elementTys: InterpreterType imarray
    | ReferenceCell of elementTy: InterpreterType
    | Function of parTys: InterpreterType imarray * returnTy: InterpreterType
    | BaseObject
    | BaseStruct
    | BaseStructEnum
    | LiteralInt32 of value: int32

    | Custom of enclosing: Choice<string imarray, InterpreterType> * name: string * isTyExt: bool * isStruct: bool * isEnum: bool * isInterface: bool * info: InterpreterTypeDefinitionInfo

    member this.IsEnum =
        match this with
        | InterpreterType.Custom(isEnum=isEnum) -> isEnum
        | _ -> false

    member this.IsTypeExtension =
        match this with
        | InterpreterType.Custom(isTyExt=isTyExt) -> isTyExt
        | _ -> false

    member this.IsInterface =
        match this with
        | InterpreterType.Custom(isInterface=isInterface) -> isInterface
        | _ -> false

    member this.IsStruct =
        match this with
        | Int8
        | UInt8
        | Int16
        | UInt16
        | Int32
        | UInt32
        | Int64
        | UInt64
        | Float32
        | Float64
        | Bool
        | Char16 -> true
        | Custom(isStruct=isStruct;info=info) -> 
            if isStruct then
                true
            elif this.IsEnum then
                match info.runtimeTyOpt with
                | Some(ty) -> ty.IsStruct
                | _ -> false
            else
                false

        | _ -> false

    member this.Fields =
        match this with
        | InterpreterType.Custom(info=info) -> info.fields :> _ seq
        | _ -> Seq.empty

    member this.Functions: InterpreterFunction seq =
        match this with
        | InterpreterType.Custom(info=info) -> info.funcs :> _ seq
        | _ -> Seq.empty

    member this.Inherits: InterpreterType imarray =
        match this with
        | InterpreterType.Custom(info=info) -> info.inherits
        | _ -> ImArray.empty

    member this.Implements: InterpreterType imarray =
        match this with
        | InterpreterType.Custom(info=info) -> info.implements
        | _ -> ImArray.empty

    member this.TryFindMostSpecificFunctionBySignatureKey(sigKey1: OlyIRFunctionSignatureKey) =
        let rec tryFind (ty: InterpreterType) =
            let results =
                ty.Functions
                |> Seq.filter (fun func ->
                    match func.SignatureKey with
                    | Some sigKey2 -> sigKey1 = sigKey2
                    | _ -> false
                )
                |> ImArray.ofSeq

            if results.IsEmpty then
                if ty.Inherits.IsEmpty then
                    None
                else
                    tryFind ty.Inherits.[0]
            elif results.Length = 1 then
                Some results[0]
            else
                OlyAssert.Fail("Ambiguous functions found.")
                
        tryFind this

    member this.TryFindOverridesFunction(virtualFunc: InterpreterFunction) =
        let funcs =
            this.Functions
            |> Seq.filter (fun func ->
                match func.Overrides with
                | Some overrides -> 
                    if virtualFunc = overrides then
                        true
                    else
                        false
                | _ -> 
                    false
            )
            |> ImArray.ofSeq
        if funcs.IsEmpty then
            None
        elif funcs.Length = 1 then
            Some funcs[0]
        else
            OlyAssert.Fail("Ambiguous functions found.")

    member this.TryFindOverridesFunctionByKey(virtualFunc: InterpreterFunction) =
        match virtualFunc.SignatureKey with
        | Some sigKey ->
            this.TryFindMostSpecificFunctionBySignatureKey(sigKey)
        | _ ->
            None

    member this.TypeDefinitionInfo =
        match this with
        | Custom(info=info) -> info
        | _ -> OlyAssert.Fail("Expected a type definition.")

[<NoEquality;NoComparison>]
type InterpreterField = InterpreterField of declaringTy: InterpreterType * name: string * ty: InterpreterType * constValueOpt: obj option * staticFieldValueOpt: obj option ref with

    member this.Name =
        match this with
        | InterpreterField(name=name) -> name

    member this.GetStaticValue() =
        match this with
        | InterpreterField(staticFieldValueOpt=valueOpt) ->
            match valueOpt.contents with
            | None -> failwith "No static value found."
            | Some value -> value

    member this.SetStaticValue(value: obj) =
        match this with
        | InterpreterField(staticFieldValueOpt=valueOpt) ->
            valueOpt.contents <- Some value

[<Sealed>]
type InterpreterFunction(env: InterpreterEnvironment,
                         name: string,
                         enclosingTy: InterpreterType,
                         isInstance: bool,
                         isCtor: bool,
                         isVirtual: bool,
                         isFinal: bool,
                         overridesOpt: InterpreterFunction option,
                         sigKey: OlyIRFunctionSignatureKey option) as this =

    let callStaticCtorOpt =
        if not isInstance && isCtor then
            Some(obj(), lazy this.CallWithStack(Stack(), ImArray.empty, true))
        else
            None

    member this.Name = name

    member this.SignatureKey = sigKey

    member this.EnclosingType: InterpreterType = enclosingTy

    member this.IsInstance = isInstance

    member this.IsVirtual = isVirtual

    member this.IsFinal = isFinal

    member this.Overrides = overridesOpt

    member val Body : Lazy<OlyIRFunctionBody<_, _, _>> option = None with get, set

    member private this.HandleAdd(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 + arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 + arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 + arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 + arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 + arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 + arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 + arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 + arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 + arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 + arg2 :> obj
        | _ ->
            failwith "Invalid 'Add'"

    member private this.HandleSubtract(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 - arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 - arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 - arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 - arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 - arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 - arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 - arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 - arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 - arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 - arg2 :> obj
        | _ ->
            failwith "Invalid 'Subtract'"

    member private this.HandleMultiply(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 * arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 * arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 * arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 * arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 * arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 * arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 * arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 * arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 * arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 * arg2 :> obj
        | _ ->
            failwith "Invalid 'Multiply'"

    member private this.HandleDivide(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 / arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 / arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 / arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 / arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 / arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 / arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 / arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 / arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 / arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 / arg2 :> obj
        | _ ->
            failwith "Invalid 'Divide'"

    member private this.HandleRemainder(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 % arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 % arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 % arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 % arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 % arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 % arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 % arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 % arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 % arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 % arg2 :> obj
        | _ ->
            failwith "Invalid 'Remainder'"

    member private this.HandleNot(arg: obj) =
        match arg with
        | (:? bool as arg) ->
            (not arg) :> obj
        | _ ->
            failwith "Invalid 'Not'"

    member private this.HandleEqual(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 = arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 = arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 = arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 = arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 = arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 = arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 = arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 = arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 = arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 = arg2 :> obj
        | (:? char as arg1), (:? char as arg2) ->
            arg1 = arg2 :> obj
        | _ ->
            obj.ReferenceEquals(arg1, arg2)

    member private this.HandleNotEqual(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 <> arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 <> arg2 :> obj
        | (:? char as arg1), (:? char as arg2) ->
            arg1 <> arg2 :> obj
        | _ ->
            not(obj.ReferenceEquals(arg1, arg2))

    member private this.HandleLessThan(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 < arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 < arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 < arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 < arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 < arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 < arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 < arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 < arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 < arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 < arg2 :> obj
        | _ ->
            failwith "Invalid 'LessThan'"

    member private this.HandleLessThanOrEqual(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 <= arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 <= arg2 :> obj
        | _ ->
            failwith "Invalid 'LessThanOrEqual'"

    member private this.HandleGreaterThan(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 > arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 > arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 > arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 > arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 > arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 > arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 > arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 > arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 > arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 > arg2 :> obj
        | _ ->
            failwith "Invalid 'GreaterThan'"

    member private this.HandleGreaterThanOrEqual(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? float32 as arg1), (:? float32 as arg2) ->
            arg1 >= arg2 :> obj
        | (:? float as arg1), (:? float as arg2) ->
            arg1 >= arg2 :> obj
        | _ ->
            failwith "Invalid 'GreaterThanOrEqual'"

    member private this.HandleBitwiseAnd(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 &&& arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 &&& arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 &&& arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 &&& arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 &&& arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 &&& arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 &&& arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 &&& arg2 :> obj
        | _ ->
            failwith "Invalid 'BitwiseAnd'"

    member private this.HandleBitwiseOr(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 ||| arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 ||| arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 ||| arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 ||| arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 ||| arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 ||| arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 ||| arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 ||| arg2 :> obj
        | _ ->
            failwith "Invalid 'BitwiseOr'"

    member private this.HandleBitwiseExclusiveOr(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? uint8 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | (:? int8 as arg1), (:? int8 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | (:? uint16 as arg1), (:? uint16 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | (:? int16 as arg1), (:? int16 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | (:? uint32 as arg1), (:? uint32 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | (:? uint64 as arg1), (:? uint64 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | (:? int64 as arg1), (:? int64 as arg2) ->
            arg1 ^^^ arg2 :> obj
        | _ ->
            failwith "Invalid 'BitwiseExclusiveOr'"

    member private this.HandleBitwiseShiftLeft(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | (:? int8 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | (:? uint16 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | (:? int16 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | (:? uint32 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | (:? uint64 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | (:? int64 as arg1), (:? int32 as arg2) ->
            arg1 <<< arg2 :> obj
        | _ ->
            failwith "Invalid 'BitwiseShiftLeft'"

    member private this.HandleBitwiseShiftRight(arg1: obj, arg2: obj) =
        match arg1, arg2 with
        | (:? uint8 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | (:? int8 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | (:? uint16 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | (:? int16 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | (:? uint32 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | (:? int32 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | (:? uint64 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | (:? int64 as arg1), (:? int32 as arg2) ->
            arg1 >>> arg2 :> obj
        | _ ->
            failwith "Invalid 'BitwiseShiftRight'"

    member private this.HandleBitwiseNot(arg1: obj) =
        match arg1 with
        | (:? uint8 as arg1) ->
            ~~~arg1 :> obj
        | (:? int8 as arg1) ->
            ~~~arg1 :> obj
        | (:? uint16 as arg1) ->
            ~~~arg1 :> obj
        | (:? int16 as arg1) ->
            ~~~arg1 :> obj
        | (:? uint32 as arg1) ->
            ~~~arg1 :> obj
        | (:? int32 as arg1) ->
            ~~~arg1 :> obj
        | (:? uint64 as arg1) ->
            ~~~arg1 :> obj
        | (:? int64 as arg1) ->
            ~~~arg1 :> obj
        | _ ->
            failwith "Invalid 'BitwiseNot'"

    /// TODO: We REALLY should get rid of this. We only need it for creating a zero'd array. We should probably push this to the front-end.
    member private this.CreateDefaultValue(resultTy: InterpreterType) =
        let asExpr irValue =
            InterpreterExpression.Value(Patterns.NoRange, irValue)

        if resultTy.IsStruct then
            match resultTy with
            | InterpreterType.Int8 ->
                InterpreterValue.Constant(OlyIRConstant.Int8(0y), resultTy) |> asExpr
            | InterpreterType.Int16 ->
                InterpreterValue.Constant(OlyIRConstant.Int16(0s), resultTy) |> asExpr
            | InterpreterType.Int32 ->
                InterpreterValue.Constant(OlyIRConstant.Int32(0), resultTy) |> asExpr
            | InterpreterType.Int64 ->
                InterpreterValue.Constant(OlyIRConstant.Int64(0L), resultTy) |> asExpr
            | InterpreterType.UInt8 ->
                InterpreterValue.Constant(OlyIRConstant.UInt8(0uy), resultTy) |> asExpr
            | InterpreterType.UInt16 ->
                InterpreterValue.Constant(OlyIRConstant.UInt16(0us), resultTy) |> asExpr
            | InterpreterType.UInt32 ->
                InterpreterValue.Constant(OlyIRConstant.UInt32(0u), resultTy) |> asExpr
            | InterpreterType.UInt64 ->
                InterpreterValue.Constant(OlyIRConstant.UInt64(0UL), resultTy) |> asExpr
            | InterpreterType.Float32 ->
                InterpreterValue.Constant(OlyIRConstant.Float32(0.0f), resultTy) |> asExpr
            | InterpreterType.Float64 ->
                InterpreterValue.Constant(OlyIRConstant.Int8(0y), resultTy) |> asExpr
            | InterpreterType.Char16 ->
                InterpreterValue.Constant(OlyIRConstant.Char16(char 0), resultTy) |> asExpr
            | _ ->
                InterpreterValue.DefaultStruct(resultTy) |> asExpr
        else
            InterpreterValue.Null(resultTy) |> asExpr

    member private this.HandleCast(arg1: obj, castToTy: InterpreterType) =
        match castToTy with
        | InterpreterType.UInt8 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                uint8 arg1 :> obj
            | (:? int8 as arg1) ->
                uint8 arg1 :> obj
            | (:? uint16 as arg1) ->
                uint8 arg1 :> obj
            | (:? int16 as arg1) ->
                uint8 arg1 :> obj
            | (:? uint32 as arg1) ->
                uint8 arg1 :> obj
            | (:? int32 as arg1) ->
                uint8 arg1 :> obj
            | (:? uint64 as arg1) ->
                uint8 arg1 :> obj
            | (:? int64 as arg1) ->
                uint8 arg1 :> obj
            | (:? float32 as arg1) ->
                uint8 arg1 :> obj
            | (:? float as arg1) ->
                uint8 arg1 :> obj
            | (:? char as arg1) ->
                uint8 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.Int8 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                int8 arg1 :> obj
            | (:? int8 as arg1) ->
                int8 arg1 :> obj
            | (:? uint16 as arg1) ->
                int8 arg1 :> obj
            | (:? int16 as arg1) ->
                int8 arg1 :> obj
            | (:? uint32 as arg1) ->
                int8 arg1 :> obj
            | (:? int32 as arg1) ->
                int8 arg1 :> obj
            | (:? uint64 as arg1) ->
                int8 arg1 :> obj
            | (:? int64 as arg1) ->
                int8 arg1 :> obj
            | (:? float32 as arg1) ->
                int8 arg1 :> obj
            | (:? float as arg1) ->
                int8 arg1 :> obj
            | (:? char as arg1) ->
                int8 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.UInt16 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                uint16 arg1 :> obj
            | (:? int8 as arg1) ->
                uint16 arg1 :> obj
            | (:? uint16 as arg1) ->
                uint16 arg1 :> obj
            | (:? int16 as arg1) ->
                uint16 arg1 :> obj
            | (:? uint32 as arg1) ->
                uint16 arg1 :> obj
            | (:? int32 as arg1) ->
                uint16 arg1 :> obj
            | (:? uint64 as arg1) ->
                uint16 arg1 :> obj
            | (:? int64 as arg1) ->
                uint16 arg1 :> obj
            | (:? float32 as arg1) ->
                uint16 arg1 :> obj
            | (:? float as arg1) ->
                uint16 arg1 :> obj
            | (:? char as arg1) ->
                uint16 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.Int16 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                int16 arg1 :> obj
            | (:? int8 as arg1) ->
                int16 arg1 :> obj
            | (:? uint16 as arg1) ->
                int16 arg1 :> obj
            | (:? int16 as arg1) ->
                int16 arg1 :> obj
            | (:? uint32 as arg1) ->
                int16 arg1 :> obj
            | (:? int32 as arg1) ->
                int16 arg1 :> obj
            | (:? uint64 as arg1) ->
                int16 arg1 :> obj
            | (:? int64 as arg1) ->
                int16 arg1 :> obj
            | (:? float32 as arg1) ->
                int16 arg1 :> obj
            | (:? float as arg1) ->
                int16 arg1 :> obj
            | (:? char as arg1) ->
                int16 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.UInt32 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                uint32 arg1 :> obj
            | (:? int8 as arg1) ->
                uint32 arg1 :> obj
            | (:? uint16 as arg1) ->
                uint32 arg1 :> obj
            | (:? int16 as arg1) ->
                uint32 arg1 :> obj
            | (:? uint32 as arg1) ->
                uint32 arg1 :> obj
            | (:? int32 as arg1) ->
                uint32 arg1 :> obj
            | (:? uint64 as arg1) ->
                uint32 arg1 :> obj
            | (:? int64 as arg1) ->
                uint32 arg1 :> obj
            | (:? float32 as arg1) ->
                uint32 arg1 :> obj
            | (:? float as arg1) ->
                uint32 arg1 :> obj
            | (:? char as arg1) ->
                uint32 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.Int32 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                int32 arg1 :> obj
            | (:? int8 as arg1) ->
                int32 arg1 :> obj
            | (:? uint16 as arg1) ->
                int32 arg1 :> obj
            | (:? int16 as arg1) ->
                int32 arg1 :> obj
            | (:? uint32 as arg1) ->
                int32 arg1 :> obj
            | (:? int32 as arg1) ->
                int32 arg1 :> obj
            | (:? uint64 as arg1) ->
                int32 arg1 :> obj
            | (:? int64 as arg1) ->
                int32 arg1 :> obj
            | (:? float32 as arg1) ->
                int32 arg1 :> obj
            | (:? float as arg1) ->
                int32 arg1 :> obj
            | (:? char as arg1) ->
                int32 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.UInt64 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                uint64 arg1 :> obj
            | (:? int8 as arg1) ->
                uint64 arg1 :> obj
            | (:? uint16 as arg1) ->
                uint64 arg1 :> obj
            | (:? int16 as arg1) ->
                uint64 arg1 :> obj
            | (:? uint32 as arg1) ->
                uint64 arg1 :> obj
            | (:? int32 as arg1) ->
                uint64 arg1 :> obj
            | (:? uint64 as arg1) ->
                uint64 arg1 :> obj
            | (:? int64 as arg1) ->
                uint64 arg1 :> obj
            | (:? float32 as arg1) ->
                uint64 arg1 :> obj
            | (:? float as arg1) ->
                uint64 arg1 :> obj
            | (:? char as arg1) ->
                uint64 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.Int64 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                int64 arg1 :> obj
            | (:? int8 as arg1) ->
                int64 arg1 :> obj
            | (:? uint16 as arg1) ->
                int64 arg1 :> obj
            | (:? int16 as arg1) ->
                int64 arg1 :> obj
            | (:? uint32 as arg1) ->
                int64 arg1 :> obj
            | (:? int32 as arg1) ->
                int64 arg1 :> obj
            | (:? uint64 as arg1) ->
                int64 arg1 :> obj
            | (:? int64 as arg1) ->
                int64 arg1 :> obj
            | (:? float32 as arg1) ->
                int64 arg1 :> obj
            | (:? float as arg1) ->
                int64 arg1 :> obj
            | (:? char as arg1) ->
                int64 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.Float32 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                float32 arg1 :> obj
            | (:? int8 as arg1) ->
                float32 arg1 :> obj
            | (:? uint16 as arg1) ->
                float32 arg1 :> obj
            | (:? int16 as arg1) ->
                float32 arg1 :> obj
            | (:? uint32 as arg1) ->
                float32 arg1 :> obj
            | (:? int32 as arg1) ->
                float32 arg1 :> obj
            | (:? uint64 as arg1) ->
                float32 arg1 :> obj
            | (:? int64 as arg1) ->
                float32 arg1 :> obj
            | (:? float32 as arg1) ->
                float32 arg1 :> obj
            | (:? float as arg1) ->
                float32 arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | InterpreterType.Float64 ->
            match arg1 with
            | (:? uint8 as arg1) ->
                float arg1 :> obj
            | (:? int8 as arg1) ->
                float arg1 :> obj
            | (:? uint16 as arg1) ->
                float arg1 :> obj
            | (:? int16 as arg1) ->
                float arg1 :> obj
            | (:? uint32 as arg1) ->
                float arg1 :> obj
            | (:? int32 as arg1) ->
                float arg1 :> obj
            | (:? uint64 as arg1) ->
                float arg1 :> obj
            | (:? int64 as arg1) ->
                float arg1 :> obj
            | (:? float32 as arg1) ->
                float arg1 :> obj
            | (:? float as arg1) ->
                float arg1 :> obj
            | _ ->
                failwith "Invalid 'HandleCast'"
        | _ ->
            // Unsafe cast
            arg1

    member this.CallStaticConstructor() =
        match callStaticCtorOpt with
        | Some(lockObj, callStaticCtor) ->
            if not callStaticCtor.IsValueCreated then
                let isEntered = Monitor.IsEntered(lockObj)
                Monitor.Enter(lockObj)
                try
                    if not isEntered then
                        callStaticCtor.Force() |> ignore
                finally
                    if not isEntered then
                        Monitor.Exit(lockObj)
        | _ ->
            failwith "Expected static constructor."

    member this.Call(args: obj imarray) =
        this.CallWithStack(Stack(), args, false)

    member this.CallWithStack(stack: Stack<obj>, args: obj imarray, willYield: bool) =
        this.CallWithStackAux(stack, args, willYield)

    member this.CallWithStackAux(stack: Stack<obj>, args: obj imarray, willYield: bool) =
        let expr =
            match this.Body with
            | None -> failwith "Body not found."
            | Some body -> body.Value.Expression

        let mutable args = args |> Array.ofSeq
        let mutable locals: obj [] = Array.init 13172 (fun _ -> null) // TODO: Optimize this! but this works for now 

        let mutable arg = null

        let copyIfStruct (arg: obj) =
            match arg with
            // TODO: If struct is read-only then we do not need to make a copy.
            | :? InterpreterInstanceOfType as arg when arg.IsStruct ->
                arg.Copy() :> obj
            | _ ->
                arg

        let box (arg: obj) =
            match arg with
            // TODO: If struct is read-only then we do not need to make a copy.
            | :? InterpreterInstanceOfType as arg when arg.IsStruct ->
                arg.Box() :> obj
            | _ ->
                arg

        let refIfStruct (arg: obj) =
            match arg with
            | :? InterpreterInstanceOfType as arg when arg.IsStruct ->
                InterpreterByReferenceOfInstance(arg) :> obj
            | _ ->
                arg

        let pushValue (stack: Stack<obj>) (value: InterpreterValue) =
            match value with
            | InterpreterValue.Constant(irConstant, _) ->
                match irConstant with
                | InterpreterConstant.Int8(value) ->
                    stack.Push(value)
                | InterpreterConstant.UInt8(value) ->
                    stack.Push(value)
                | InterpreterConstant.Int16(value) ->
                    stack.Push(value)
                | InterpreterConstant.UInt16(value) ->
                    stack.Push(value)
                | InterpreterConstant.Int32(value) ->
                    stack.Push(value)
                | InterpreterConstant.UInt32(value) ->
                    stack.Push(value)
                | InterpreterConstant.Int64(value) ->
                    stack.Push(value)
                | InterpreterConstant.UInt64(value) ->
                    stack.Push(value)
                | InterpreterConstant.Float32(value) ->
                    stack.Push(value)
                | InterpreterConstant.Float64(value) ->
                    stack.Push(value)
                | InterpreterConstant.True(_) ->
                    stack.Push(true)
                | InterpreterConstant.False(_) ->
                    stack.Push(false)
                | InterpreterConstant.Char16(value) ->
                    stack.Push(value)
                | InterpreterConstant.Utf16(value) ->
                    stack.Push(value)
                | InterpreterConstant.Array _ ->
                    raise(System.NotImplementedException())
                | InterpreterConstant.Variable _ ->
                    raise(System.NotSupportedException("constant variable"))
                | InterpreterConstant.External _ ->
                    raise(System.NotImplementedException())
            | InterpreterValue.Local(n, _) ->
                stack.Push(locals.[n] |> copyIfStruct)
            | InterpreterValue.LocalAddress(n, _, _) ->
                stack.Push(InterpreterByReferenceOfLocal(locals, n))
            | InterpreterValue.Argument(n, _) ->
                stack.Push(args.[n] |> copyIfStruct)
            | InterpreterValue.ArgumentAddress(n, _, _) ->
                stack.Push(InterpreterByReferenceOfArgument(args, n))
            | InterpreterValue.Unit _ ->
                stack.Push(null)
            | InterpreterValue.Null _ ->
                stack.Push(null)
            | InterpreterValue.DefaultStruct(resultTy) ->
                match resultTy with
                | ty when ty.IsStruct ->
                    let instance = InterpreterInstanceOfType(resultTy, false, true, resultTy.Inherits, resultTy.Implements)
                    for field in resultTy.Fields do
                        instance.SetFieldState(field.Name, null)
                    stack.Push(instance)
                | _ ->
                    OlyAssert.Fail("Expected struct type.")
            | InterpreterValue.StaticField(irField, _) ->
                irField.EmittedField.GetStaticValue()
                |> stack.Push
            | InterpreterValue.Function(func, resultTy) ->
                InterpreterInstanceOfFunctionType(resultTy, func)
                |> stack.Push
            | _ ->
                raise(System.NotImplementedException(sprintf "InterpreterValue.%A" value))

        let rec evalArg (stack: Stack<obj>) (argExpr: InterpreterExpression) =
            evalExpr stack argExpr
            stack.Pop()

        and evalArgs (stack: Stack<obj>) (argExprs: InterpreterExpression imarray) =
            argExprs
            |> ImArray.map (fun argExpr ->
                evalArg stack argExpr
            )     

        and evalOp (stack: Stack<obj>) (op: InterpreterOperation) =
            match op with
            | InterpreterOperation.Ignore(argExpr, _) ->
                evalArg stack argExpr
                |> ignore

            | InterpreterOperation.Witness(bodyExpr, witnessTy, _) ->
                let ctor = witnessTy.Functions |> Seq.find (fun x -> x.Name = "__oly_instance_ctor")
                evalExpr stack (OlyIRExpression.Operation(OlyIRDebugSourceTextRange.Empty, OlyIROperation.New(OlyIRFunction(ctor), ImArray.createOne bodyExpr, witnessTy)))

            | InterpreterOperation.Store(n, argExpr, _) ->
                locals.[n] <- evalArg stack argExpr

            | InterpreterOperation.StoreArgument(n, argExpr, _) ->
                args.[n] <- evalArg stack argExpr

            | InterpreterOperation.Box(argExpr, _) ->
                stack.Push(box (evalArg stack argExpr))

            | InterpreterOperation.Unbox(argExpr, _) ->
                stack.Push(evalArg stack argExpr)

            | InterpreterOperation.Upcast(argExpr, _) ->
                stack.Push(evalArg stack argExpr)

            | InterpreterOperation.Cast(argExpr, resultTy) ->
                stack.Push(this.HandleCast(evalArg stack argExpr, resultTy))

            | InterpreterOperation.Add(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleAdd(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.Subtract(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleSubtract(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.Multiply(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleMultiply(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.Divide(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleDivide(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.Remainder(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleRemainder(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.Not(argExpr, _) ->
                arg <- evalArg stack argExpr

                let result = this.HandleNot(arg)

                stack.Push(result)

            | InterpreterOperation.Equal(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleEqual(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.NotEqual(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleNotEqual(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.Utf16Equal(argExpr1, argExpr2, _) ->
                stack.Push(String.Equals(evalArg stack argExpr1 :?> string, evalArg stack argExpr2 :?> string))

            | InterpreterOperation.LessThan(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleLessThan(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.LessThanOrEqual(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleLessThanOrEqual(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.GreaterThan(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleGreaterThan(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.GreaterThanOrEqual(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleGreaterThanOrEqual(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.BitwiseAnd(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleBitwiseAnd(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.BitwiseOr(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleBitwiseOr(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.BitwiseExclusiveOr(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleBitwiseExclusiveOr(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.BitwiseShiftLeft(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleBitwiseShiftLeft(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.BitwiseShiftRight(argExpr1, argExpr2, _) ->
                stack.Push(this.HandleBitwiseShiftRight(evalArg stack argExpr1, evalArg stack argExpr2))

            | InterpreterOperation.BitwiseNot(argExpr1, _) ->
                stack.Push(this.HandleBitwiseNot(evalArg stack argExpr1))

            | InterpreterOperation.Print(argExpr, _) ->
                arg <- evalArg stack argExpr

                match arg with
                | null -> ()
                | _ ->
                    let enumTyOpt =
                        match argExpr with
                        | InterpreterExpression.Operation(_, InterpreterOperation.Box(expr, _)) ->
                            Some expr.ResultType
                        | _ ->
                            None
                    
                    let text =
                        match enumTyOpt with
                        | Some enumTy ->
                            match enumTy with
                            | InterpreterType.Custom(info = { fields=fields }) ->
                                let fieldNameOpt =
                                    fields
                                    |> Seq.tryPick (fun field ->
                                        match field with
                                        | InterpreterField.InterpreterField(constValueOpt=Some value) when value.Equals(arg) ->
                                            Some field.Name
                                        | _ ->
                                            None
                                    )
                                match fieldNameOpt with
                                | Some fieldName -> fieldName
                                | _ -> arg.ToString()
                            | _ ->
                                arg.ToString()
                        | _ ->
                            arg.ToString()
                    env.StandardOut.Append(text) |> ignore

            | InterpreterOperation.Call(func, argExprs, _) ->            
                func.EmittedFunction.CallWithStack(stack, evalArgs stack argExprs, false)

            | InterpreterOperation.CallVirtual(irFunc, argExprs, _) ->
                let func = irFunc.EmittedFunction
                let args = evalArgs stack argExprs

                // TODO: This is slow, we need something with constant time via slot.
                //       But it works for now.
                let overridesOpt =
                    if func.IsInstance then
                        let receiver = args.[0]
                        match receiver with
                        | :? InterpreterInstanceOfType as receiver ->
                            let receiverTy = receiver.Type
                            let resultOpt = receiverTy.TryFindOverridesFunction(func)
                            if resultOpt.IsSome then
                                resultOpt
                            else
                                if receiverTy.IsTypeExtension then
                                    match receiver.GetTypeExtensionInstance() with
                                    | :? InterpreterInstanceOfType as instance ->
                                        let resultOpt = instance.Type.TryFindOverridesFunction(func)
                                        if resultOpt.IsSome then 
                                            resultOpt
                                        elif not func.EnclosingType.IsInterface && func.IsInstance && func.IsVirtual then 
                                            // Fallback - look for a function that can override it by key.
                                            // This one is a little special.
                                            // TODO: Describe why this is special.
                                            instance.Type.TryFindOverridesFunctionByKey(func)
                                        else
                                            // Fallback - look for a function that can override it by key.
                                            receiverTy.TryFindOverridesFunctionByKey(func)
                                    | _ ->
                                        // Fallback - look for a function that can override it by key.
                                        receiverTy.TryFindOverridesFunctionByKey(func)
                                else
                                    // Fallback - look for a function that can override it by key.
                                    receiverTy.TryFindOverridesFunctionByKey(func)
                        | _ ->
                            None
                    else
                        None
                        
                match overridesOpt with
                | Some(overrides) ->
                    overrides.CallWithStack(stack, args, willYield)
                | _ ->
                    func.CallWithStack(stack, args, willYield)

            | InterpreterOperation.New(ctor, argExprs, resultTy) ->
                let ctor = ctor.EmittedFunction
                let thisArg =
                    match resultTy with
                    | InterpreterType.Custom(_, _, isTyExt, isStruct, isEnum, isInterface, info) ->
                        let fields = info.fields
                        let inherits = info.inherits
                        let implements = info.implements
                        let instance = InterpreterInstanceOfType(resultTy, isTyExt, isStruct, inherits, implements)
                        for field in fields do
                            instance.SetFieldState(field.Name, null)
                        instance :> obj
                    | _ ->
                        failwith "Invalid type to create an instance."

                let args = evalArgs stack argExprs            
                ctor.CallWithStack(stack, ImArray.prependOne (refIfStruct thisArg) args, false)
                stack.Push(thisArg)

            | InterpreterOperation.StoreField(irField, receiverExpr, argExpr, _) ->
                let field = irField.EmittedField

                let receiver = evalArg stack receiverExpr
                arg <- evalArg stack argExpr

                match receiver with
                | :? InterpreterInstanceOfType as receiver ->
                    receiver.SetFieldState(field.Name, arg)
                | :? InterpreterByReferenceOfInstance as receiver ->
                    receiver.InstanceOfType.SetFieldState(field.Name, arg)
                | _ ->
                    failwith "Invalid receiver instance."

            | InterpreterOperation.LoadField(irField, receiverExpr, _) ->
                let field = irField.EmittedField

                let receiver = evalArg stack receiverExpr
                match receiver with
                | :? InterpreterInstanceOfType as arg ->
                    stack.Push(arg.GetFieldState(field.Name) |> copyIfStruct)
                | :? InterpreterByReferenceOfInstance as arg ->
                    stack.Push(arg.InstanceOfType.GetFieldState(field.Name))
                | _ ->
                    failwith "Invalid receiver instance."

            | InterpreterOperation.LoadFieldAddress(irField, receiverExpr, _, _) ->
                let field = irField.EmittedField

                let receiver = evalArg stack receiverExpr
                match receiver with
                | :? InterpreterInstanceOfType as arg ->
                    stack.Push(arg.GetFieldStateAddress(field.Name))
                | :? InterpreterByReferenceOfInstance as arg ->
                    stack.Push(arg.InstanceOfType.GetFieldStateAddress(field.Name))
                | _ ->
                    failwith "Invalid receiver instance."

            | InterpreterOperation.CallIndirect(receiver=receiverExpr; args=argExprs) ->
                match evalArg stack receiverExpr with
                | :? InterpreterInstanceOfClosureType as receiver ->
                    let thisArg = receiver.InvokeThisArgument
                    let args = evalArgs stack argExprs 
                    receiver.InvokeFunction.CallWithStack(stack, ImArray.prependOne thisArg args, false)
                | :? InterpreterInstanceOfFunctionType as func ->
                    let args = evalArgs stack argExprs 
                    func.InvokeFunction.CallWithStack(stack, args, false)
                | res ->
                    failwithf "Invalid indirect call."

            | InterpreterOperation.NewRefCell(_, argExpr, resultTy) ->
                let instance = InterpreterInstanceOfType(resultTy, false, false, ImArray.empty, ImArray.empty)
                instance.SetFieldState("contents", evalArg stack argExpr)
                stack.Push(instance :> obj)

            | InterpreterOperation.LoadRefCellContents(receiverExpr, _) ->
                match evalArg stack receiverExpr with
                | :? InterpreterInstanceOfType as receiver ->
                    stack.Push(receiver.GetFieldState("contents"))
                | _ ->
                    failwith "Invalid 'LoadRefCellContents' operation."

            | InterpreterOperation.LoadRefCellContentsAddress(receiverExpr, _, _) ->
                match evalArg stack receiverExpr with
                | :? InterpreterInstanceOfType as receiver ->
                    stack.Push(receiver.GetFieldStateAddress("contents"))
                | _ ->
                    failwith "Invalid 'LoadRefCellContentsAddress' operation."

            | InterpreterOperation.StoreRefCellContents(receiverExpr, argExpr, _) ->
                match evalArg stack receiverExpr with
                | :? InterpreterInstanceOfType as receiver ->
                    receiver.SetFieldState("contents", evalArg stack argExpr)
                | _ ->
                    failwith "Invalid 'StoreRefCellContents' operation."

            | InterpreterOperation.NewTuple(elementTys, argExprs, resultTy) ->
                let args = evalArgs stack argExprs

                let instance = InterpreterInstanceOfType(resultTy, false, false, ImArray.empty, ImArray.empty)
                for i = 0 to elementTys.Length - 1 do
                    let name = i.ToString()
                    let state = args.[i]
                    instance.SetFieldState(name, state)

                stack.Push(instance :> obj)

            | InterpreterOperation.LoadFromAddress(argExpr, _) ->
                match evalArg stack argExpr with
                | :? InterpreterByReferenceOfInstance as arg ->
                    stack.Push(arg.Instance |> copyIfStruct)
                | _ ->
                    failwith "Invalid 'LoadFromAddress' operation."

            | InterpreterOperation.StoreToAddress(argExpr1, argExpr2, _) ->
                match evalArg stack argExpr1 with
                | :? InterpreterByReferenceOfInstance as arg1 ->
                    arg1.Instance <- evalArg stack argExpr2
                | _ ->
                    failwith "Invalid 'StoreToAddress' operation."

            | InterpreterOperation.LoadTupleElement(argExpr, index, _) ->
                match evalArg stack argExpr with
                | :? InterpreterInstanceOfType as arg ->
                    arg.GetFieldState(index.ToString())
                    |> stack.Push
                | _ ->
                    failwith "Invalid 'LoadTupleItem' operation."

            | InterpreterOperation.StoreStaticField(irField, argExpr, _) ->
                irField.EmittedField.SetStaticValue(evalArg stack argExpr)

            | InterpreterOperation.CallStaticConstructor(irFunc, _) ->
                irFunc.EmittedFunction.CallStaticConstructor()

            | InterpreterOperation.NewArray(_, _, irArgExprs, _) ->
                let arr = Array.zeroCreate irArgExprs.Length
                
                for i = 0 to irArgExprs.Length - 1 do
                    arr[i] <- evalArg stack irArgExprs[i]

                stack.Push(arr)

            | InterpreterOperation.NewMutableArray(elementTy, irSizeArgExpr, _) ->
                Array.init (evalArg stack irSizeArgExpr :?> int) (fun _ ->
                    evalArg stack (this.CreateDefaultValue(elementTy))
                )
                |> stack.Push

            | InterpreterOperation.LoadArrayLength(irReceiverExpr, rank, _) ->
                match evalArg stack irReceiverExpr with
                | :? (obj[]) as receiver when rank = 1 ->
                    receiver.Length
                    |> stack.Push
                | _ ->
                    raise(System.NotImplementedException(sprintf "InterpreterOperation.%A" op))

            | InterpreterOperation.LoadArrayElement(irReceiverExpr, irIndexArgExprs, _) ->
                match evalArg stack irReceiverExpr with
                | :? (obj[]) as receiver when irIndexArgExprs.Length = 1 ->
                    receiver[evalArg stack irIndexArgExprs[0] :?> int]
                    |> stack.Push
                | _ ->
                    raise(System.NotImplementedException(sprintf "InterpreterOperation.%A" op))

            | InterpreterOperation.LoadArrayElementAddress(irReceiverExpr, irIndexArgExprs, _, _) ->
                match evalArg stack irReceiverExpr with
                | :? (obj[]) as receiver when irIndexArgExprs.Length = 1 ->
                    InterpreterByReferenceOfArrayElement(evalArg stack irIndexArgExprs[0] :?> int, receiver)
                    |> stack.Push
                | _ ->
                    raise(System.NotImplementedException(sprintf "InterpreterOperation.%A" op))

            | InterpreterOperation.StoreArrayElement(irReceiverExpr, irIndexArgExprs, irArgExpr, _) ->
                match evalArg stack irReceiverExpr with
                | :? (obj[]) as receiver when irIndexArgExprs.Length = 1 ->
                    receiver[evalArg stack irIndexArgExprs[0] :?> int] <- evalArg stack irArgExpr
                | _ ->
                    raise(System.NotImplementedException(sprintf "InterpreterOperation.%A" op))

            | InterpreterOperation.LoadFunction(irFunc, irArgExpr, _) ->
                InterpreterInstanceOfClosureType(
                    irFunc.EmittedFunction.EnclosingType, 
                    false,
                    evalArg stack irArgExpr,
                    irFunc.EmittedFunction)
                |> stack.Push

            | _ ->
                raise(System.NotImplementedException(sprintf "InterpreterOperation.%A" op))

        and evalExpr (stack: Stack<obj>) (expr: InterpreterExpression) =
            match expr with
            | InterpreterExpression.Try _ ->
                raise(NotImplementedException("'Try' expression"))

            | InterpreterExpression.IfElse(predicateExpr, trueExpr, falseExpr, _) ->
                evalExpr stack predicateExpr
                match stack.Pop() with
                | :? bool as value ->
                    if value then
                        evalExpr stack trueExpr
                    else
                        evalExpr stack falseExpr
                | _ ->
                    failwith "Expected a 'bool'"

            | InterpreterExpression.While(predicateExpr, bodyExpr, _) ->
                let mutable predicateValue =
                    evalExpr stack predicateExpr
                    match stack.Pop() with
                    | :? bool as value -> value
                    | _ -> failwith "Expected a 'bool'"

                while predicateValue do
                    evalExpr stack bodyExpr
                    evalExpr stack predicateExpr

                    predicateValue <-
                        match stack.Pop() with
                        | :? bool as value -> value
                        | _ -> failwith "Expected a 'bool'"

            | InterpreterExpression.Let(_, n, irRhsExpr, irBodyExpr) ->
                evalExpr stack irRhsExpr
                locals.[n] <- stack.Pop()
                evalExpr stack irBodyExpr

            | InterpreterExpression.Value(_, value) ->
                pushValue stack value

            | InterpreterExpression.Operation(_, op) ->
                evalOp stack op

            | InterpreterExpression.Sequential(expr1, expr2) ->
                evalExpr stack expr1
                evalExpr stack expr2

            | InterpreterExpression.None(_) ->
                ()

        evalExpr stack expr

type InterpreterExpression = OlyIRExpression<InterpreterType, InterpreterFunction, InterpreterField>
type InterpreterOperation = OlyIROperation<InterpreterType, InterpreterFunction, InterpreterField>
type InterpreterValue = OlyIRValue<InterpreterType, InterpreterFunction, InterpreterField>
type InterpreterConstant = OlyIRConstant<InterpreterType, InterpreterFunction>

type InterpreterInstanceOfType private (ty: InterpreterType, isTyExt: bool, isStruct: bool, fieldStates: ConcurrentDictionary<string, obj>, inherits: InterpreterType imarray, implements: InterpreterType imarray) =

    member this.SetFieldState(name, state) =
        fieldStates.[name] <- state

    member this.GetFieldState(name): obj =
        if isTyExt then
            match fieldStates.["__oly_instance_value"] with
            | :? InterpreterInstanceOfType as state ->
                state.GetFieldState(name)
            | _ ->
                failwithf "Unable to find field: '%s'." name
        else
            fieldStates.[name]

    member this.GetFieldStateAddress(name): obj =
        if isTyExt then
            match fieldStates.["__oly_instance_value"] with
            | :? InterpreterInstanceOfType as state ->
                state.GetFieldStateAddress(name)
            | _ ->
                failwithf "Unable to find field: '%s'." name
        else
            InterpreterByReferenceOfInstanceField(name, fieldStates)

    member this.GetTypeExtensionInstance() : obj =
        fieldStates.["__oly_instance_value"]

    member _.Type: InterpreterType = ty

    member _.IsStruct = isStruct

    member _.IsTypeExtension = isTyExt

    member _.Extends = inherits

    member _.Implements = implements

    member _.Copy(): InterpreterInstanceOfType =
        let copiedInstance =
            fieldStates
            |> Seq.map (fun pair ->
                match pair.Value with
                | :? InterpreterInstanceOfType as instance when instance.IsStruct ->
                    KeyValuePair(pair.Key, instance.Copy() :> obj)
                | _ ->
                    pair
            )
            |> ConcurrentDictionary
        InterpreterInstanceOfType(ty, isTyExt, isStruct, copiedInstance, inherits, implements)

    member _.Box(): InterpreterInstanceOfType =
        let copiedInstance =
            fieldStates
            |> Seq.map (fun pair ->
                match pair.Value with
                | :? InterpreterInstanceOfType as instance when instance.IsStruct ->
                    KeyValuePair(pair.Key, instance.Copy() :> obj)
                | _ ->
                    pair
            )
            |> ConcurrentDictionary
        InterpreterInstanceOfType(ty, isTyExt, false, copiedInstance, inherits, implements)

    new(ty, isTyExt, isStruct, inherits, implements) =
        InterpreterInstanceOfType(ty, isTyExt, isStruct, ConcurrentDictionary(), inherits, implements)

type InterpreterInstanceOfClosureType (ty, isStruct: bool, thisArg: obj, invoke: InterpreterFunction) =
    inherit InterpreterInstanceOfType(ty, false, isStruct, ImArray.empty, ImArray.empty)

    member _.InvokeThisArgument: obj = thisArg
    member _.InvokeFunction: InterpreterFunction = invoke

type InterpreterInstanceOfFunctionType (ty, invoke: InterpreterFunction) =
    inherit InterpreterInstanceOfType(ty, false, false, ImArray.empty, ImArray.empty)

    member _.InvokeFunction: InterpreterFunction = invoke
        
type InterpreterByReferenceOfInstance(instance: obj) =

    let mutable instance = instance

    abstract Instance : obj with get, set
    default _.Instance
        with get() = instance
        and set value = instance <- value

    member this.InstanceOfType: InterpreterInstanceOfType = this.Instance :?> InterpreterInstanceOfType  

type InterpreterByReferenceOfInstanceField(fieldName, lookup: ConcurrentDictionary<string, obj>) =
    inherit InterpreterByReferenceOfInstance(null)

    override this.Instance
        with get() = lookup[fieldName]
        and set value = lookup[fieldName] <- value

type InterpreterByReferenceOfArrayElement(index, arr: obj[]) =
    inherit InterpreterByReferenceOfInstance(null)

    override this.Instance
        with get() = arr[index]
        and set value = arr[index] <- value

[<Sealed>]
type InterpreterByReferenceOfLocal(locals: obj [], n: int32) =
    inherit InterpreterByReferenceOfInstance(Unchecked.defaultof<_>)

    override _.Instance
        with get() = locals.[n]
        and set value = locals.[n] <- value

    member this.InstanceOfType: InterpreterInstanceOfType = this.Instance :?> InterpreterInstanceOfType  

[<Sealed>]
type InterpreterByReferenceOfArgument(arguments: obj [], n: int32) =
    inherit InterpreterByReferenceOfInstance(Unchecked.defaultof<_>)

    override _.Instance
        with get() = arguments.[n]
        and set value = arguments.[n] <- value

    member this.InstanceOfType: InterpreterInstanceOfType = this.Instance :?> InterpreterInstanceOfType  
        
[<Sealed>]
type InterpreterRuntimeEmitter() =

    let env = InterpreterEnvironment()

    let mutable entryPoint: InterpreterFunction voption = ValueNone

    member this.Run(args: obj imarray) =
        match entryPoint with
        | ValueNone -> failwith "Entry point not found."
        | ValueSome entryPoint ->
            entryPoint.Call(args)

    member this.StandardOut = env.StandardOut.ToString()

    interface IOlyRuntimeEmitter<InterpreterType, InterpreterFunction, InterpreterField> with

        member this.Initialize(_) = ()

        member this.EmitFunctionInstance(_, func: InterpreterFunction, tyArgs: _): InterpreterFunction = 
            raise(System.NotSupportedException())

        member this.EmitFunctionReference(_, func: InterpreterFunction): InterpreterFunction = 
            raise(System.NotSupportedException())

        member this.EmitTypeGenericInstance(ty: InterpreterType, tyArgs: imarray<InterpreterType>): InterpreterType = 
            raise(System.NotSupportedException())

        member this.EmitExternalType(_, _, _, _, _, _, _, _): InterpreterType = 
            raise(NotSupportedException())

        member _.EmitExportedProperty(_, _, _, _, _, _) =
            raise(NotSupportedException())

        member this.EmitField(enclosingTy, flags, name: string, fieldTy: InterpreterType, _, irConstValueOpt): InterpreterField = 
            match enclosingTy with
            | InterpreterType.Custom(info = { fields=fields }) ->
                let constValueOpt =
                    match irConstValueOpt with
                    | Some(OlyIRConstant.UInt8(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Int8(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.UInt16(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Int16(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.UInt32(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Int32(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.UInt64(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Int64(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Float32(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Float64(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Char16(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Utf16(value)) -> Some(value :> obj)
                    | Some(OlyIRConstant.Array(_, elements)) -> Some(elements :> obj)
                    | Some(OlyIRConstant.True) -> Some(true :> obj)
                    | Some(OlyIRConstant.False) -> Some(false :> obj)
                    | Some(OlyIRConstant.External _) ->
                        raise(NotSupportedException("External constants are not supported in the interpreter."))
                    | _ -> 
                        None
                let field = InterpreterField(enclosingTy, name, fieldTy, constValueOpt, ref None)
                fields.Add(field)
                field
            | _ ->
                raise (NotImplementedException())

        member this.EmitFunctionDefinition(_, enclosingTy, flags, name: string, tyPars, pars, returnTy, overridesOpt, sigKey, _): InterpreterFunction = 
            if not tyPars.IsEmpty then
                raise(NotSupportedException())

            match enclosingTy with
            | InterpreterType.Custom(info = { funcs=funcs }) ->
                let func = InterpreterFunction(env, name, enclosingTy, not flags.IsStatic, flags.IsConstructor, flags.IsVirtual, flags.IsFinal, overridesOpt, Some sigKey)
                if flags.IsEntryPoint then
                    entryPoint <- ValueSome func

                let alreadyExists =
                    funcs
                    |> Seq.exists (
                        fun func ->
                            match func.SignatureKey with
                            | Some(sigKey2) -> sigKey.Equals(sigKey2)
                            | _ -> false
                    )

                if alreadyExists then
                    failwith $"Function '{name}' already exists on type."

                funcs.Add(func)
                func
            | _ ->
                failwith "Invalid function."

        member this.EmitFunctionBody(irFuncBody: Lazy<_>, _, func: InterpreterFunction): unit =
            func.Body <- Some irFuncBody

        member this.EmitTypeDefinitionInfo(ty, enclosing: Choice<string imarray, InterpreterType>, kind: OlyILEntityKind, flags: OlyIRTypeFlags, name: string, tyPars: imarray<OlyIRTypeParameter<InterpreterType>>, extends, implements, _, runtimeTyOpt) = 
            let funcs = ty.TypeDefinitionInfo.funcs
            let fields = ty.TypeDefinitionInfo.fields
            ty.TypeDefinitionInfo.runtimeTyOpt <- runtimeTyOpt
            ty.TypeDefinitionInfo.inherits <- extends
            ty.TypeDefinitionInfo.implements <- implements
            if kind = OlyILEntityKind.TypeExtension && extends.Length = 1 then
                let valueFieldName = "__oly_instance_value"
                let valueField = InterpreterField(ty, valueFieldName, extends.[0], None, ref None)
                fields.Add(valueField)

                let ctorName = "__oly_instance_ctor"
                let ctor = InterpreterFunction(env, ctorName, ty, true, true, false, false, None, None)
                ctor.Body <-
                    let arg0 = OlyIRExpression.Value(OlyIRDebugSourceTextRange.Empty, OlyIRValue.Argument(0, ty))
                    let arg1 = OlyIRExpression.Value(OlyIRDebugSourceTextRange.Empty, OlyIRValue.Argument(1, extends.[0]))
                    let body =
                        OlyIRExpression.CreateSequential(
                            [
                                OlyIRExpression.Operation(OlyIRDebugSourceTextRange.Empty, OlyIROperation.StoreField(OlyIRField(valueField), arg0, arg1, InterpreterType.Void))
                            ]
                            |> ImArray.ofSeq
                        )
                    let argFlags = ImArray.init 2 (fun _ -> OlyIRLocalFlags.None) // TODO: This is not accurate. Consider never having to include the argflags.
                    lazy OlyIRFunctionBody(body, argFlags, ImArray.empty) |> Some
                funcs.Add(ctor)
            

        member this.EmitTypeDefinition(enclosing: Choice<string imarray, InterpreterType>, kind: OlyILEntityKind, _flags: OlyIRTypeFlags, name: string, _tyParCount): InterpreterType = 
            let isStruct = (kind = OlyILEntityKind.Struct)
            InterpreterType.Custom(
                enclosing, 
                name, 
                (kind = OlyILEntityKind.TypeExtension), 
                isStruct, 
                (kind = OlyILEntityKind.Enum), 
                (kind = OlyILEntityKind.Interface), 
                InterpreterTypeDefinitionInfo.Default
            )

        member this.EmitTypeBool(): InterpreterType = 
            InterpreterType.Bool

        member this.EmitTypeByRef(arg1: InterpreterType, arg2: OlyIRByRefKind): InterpreterType = 
            match arg2 with
            | OlyIRByRefKind.ReadWrite ->
                InterpreterType.ByReference(arg1)
            | OlyIRByRefKind.Read ->
                InterpreterType.InReference(arg1)

        member this.EmitTypeChar16(): InterpreterType = 
            InterpreterType.Char16

        member this.EmitTypeFloat32(): InterpreterType = 
            InterpreterType.Float32

        member this.EmitTypeFloat64(): InterpreterType = 
            InterpreterType.Float64

        member this.EmitTypeFunction(inputTys: imarray<InterpreterType>, outputTy: InterpreterType): InterpreterType = 
            InterpreterType.Function(inputTys, outputTy)

        member this.EmitTypeHigherVariable(index: int32, tyInst: imarray<InterpreterType>, _): InterpreterType = 
            raise(System.NotSupportedException("Second-Order Generics"))

        member this.EmitTypeInt16(): InterpreterType = 
            InterpreterType.Int16

        member this.EmitTypeInt32(): InterpreterType = 
            InterpreterType.Int32

        member this.EmitTypeInt64(): InterpreterType = 
            InterpreterType.Int64

        member this.EmitTypeInt8(): InterpreterType = 
            InterpreterType.Int8

        member this.EmitTypeConstantInt32(value: int32): InterpreterType = 
            InterpreterType.LiteralInt32(value)

        member this.EmitTypeBaseObject(): InterpreterType = 
            InterpreterType.BaseObject

        member this.EmitTypeRefCell(ty: InterpreterType): InterpreterType = 
            InterpreterType.ReferenceCell(ty)

        member this.EmitTypeTuple(elementTys: imarray<InterpreterType>, _): InterpreterType = 
            InterpreterType.Tuple(elementTys)

        member this.EmitTypeUInt16(): InterpreterType = 
            InterpreterType.UInt16

        member this.EmitTypeUInt32(): InterpreterType = 
            InterpreterType.UInt32

        member this.EmitTypeUInt64(): InterpreterType = 
            InterpreterType.Int64

        member this.EmitTypeUInt8(): InterpreterType = 
            InterpreterType.UInt8

        member this.EmitTypeUnit(): InterpreterType = 
            InterpreterType.Unit

        member this.EmitTypeUtf16(): InterpreterType = 
            InterpreterType.Utf16

        member this.EmitTypeVariable(index: int32, _): InterpreterType = 
            raise(System.NotSupportedException("Generics"))

        member this.EmitTypeVoid(): InterpreterType = 
            InterpreterType.Void

        member this.EmitTypeNativeInt() =
            raise(System.NotImplementedException())

        member this.EmitTypeNativeUInt() =
            raise(System.NotImplementedException())

        member this.EmitTypeNativePtr(elementTy) =
            raise(System.NotImplementedException())

        member this.EmitTypeNativeFunctionPtr(_, _, _) =
            raise(System.NotImplementedException())

        member this.EmitTypeArray(elementTy, _, _) =
            InterpreterType.BaseObject // TODO:

