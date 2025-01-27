﻿module internal rec Oly.Compiler.Internal.SymbolBuilders

open System.Diagnostics
open Oly.Core
open Oly.Compiler.Internal.Symbols

[<Sealed;DebuggerDisplay("{DebugName}")>]
type EntitySymbolBuilder private (
                                 ent: EntityDefinitionSymbol,
                                 bindings: (BindingInfoSymbol * bool) imarray,
                                 ents: EntitySymbolBuilder imarray,
                                 attrsHole: Ref<imarray<AttributeSymbol>>,
                                 funcsHole: Ref<imarray<FunctionSymbol>>,
                                 fieldsHole: Ref<imarray<IFieldSymbol>>,
                                 propsHole: Ref<imarray<PropertySymbol>>,
                                 patsHole: Ref<imarray<PatternSymbol>>,
                                 tyParsHole: Ref<imarray<TypeParameterSymbol>>,
                                 extendsHole: Ref<imarray<TypeSymbol>>,
                                 implementsHole: Ref<imarray<TypeSymbol>>,
                                 runtimeTyOptHole: Ref<option<TypeSymbol>>,
                                 entsHole: ResizeArray<EntitySymbol>) =

    let mutable bindings: (BindingInfoSymbol * bool) imarray = bindings
    let mutable ents: EntitySymbolBuilder imarray = ents

    do OlyAssert.True(ent.IsFormal)

    member private _.DebugName = ent.Name

    member _.Entity: EntityDefinitionSymbol = ent
    member _.Bindings : (BindingInfoSymbol * bool) imarray = bindings
    member _.NestedEntityBuilders : EntitySymbolBuilder imarray = ents

    member _.SetAttributes(pass: CompilerPass, attrs) = 
        match pass with
        | Pass0
        | Pass3 ->
            attrsHole.contents <- attrs
        | _ ->
            failwith "Invalid pass."

    member _.SetExtends(pass: CompilerPass, extends: TypeSymbol imarray) = 
#if DEBUG
        extends
        |> ImArray.iter (fun ty -> OlyAssert.True(ty.IsSolved))
#endif
        match pass with
        | Pass1
        | Pass0 (*Pass0 is for intrinsic types*) ->
            extendsHole.contents <- extends
        | _ ->
            failwith "Invalid pass."

    member _.SetImplements(pass: CompilerPass, implements: TypeSymbol imarray) = 
#if DEBUG
        implements
        |> ImArray.iter (fun ty -> OlyAssert.True(ty.IsSolved))
#endif
        match pass with
        | Pass1 ->
            implementsHole.contents <- implements
        | _ ->
            failwith "Invalid pass."

    member _.SetRuntimeType(pass: CompilerPass, runtimeTy: TypeSymbol) =
        OlyAssert.True(runtimeTy.IsSolved)
        match pass with
        | Pass1 ->
            runtimeTyOptHole.contents <- Some runtimeTy
        | _ ->
            failwith "Invalid pass."

    member _.SetBindings(pass: CompilerPass, bindingsToSet) = 
        match pass with
        | Pass2 ->
            bindings <- bindingsToSet
        | _ ->
            failwith "Invalid pass."

    member _.SetFunctions(pass: CompilerPass, funcs) = 
        match pass with
        | Pass2 ->
            funcsHole.contents <- funcs
        | _ ->
            failwith "Invalid pass."

    member this.SetFields(pass: CompilerPass, fields: IFieldSymbol imarray) =
        match pass with
        | Pass2 ->
            fieldsHole.contents <- fields
        | _ ->
            failwith "Invalid pass."

    member _.SetProperties(pass: CompilerPass, props) = 
        match pass with
        | Pass2 ->
            propsHole.contents <- props
        | _ ->
            failwith "Invalid pass."

    member _.SetPatterns(pass: CompilerPass, pats) = 
        match pass with
        | Pass2 ->
            patsHole.contents <- pats
        | _ ->
            failwith "Invalid pass."

    member _.SetEntities(pass: CompilerPass, entsToSet) = 
        match pass with
        | Pass0 ->
            ents <- entsToSet 
            ent.ClearEntities(pass)
            entsHole.Clear()
            entsToSet
            |> ImArray.iter (fun x -> entsHole.Add(x.Entity))
        | _ ->
            failwith "Invalid pass."

    member _.SetTypeParameters(pass: CompilerPass, tyParsToSet: TypeParameterSymbol imarray) =
#if DEBUG
        if ent.Enclosing.TypeParameters.Length > tyParsToSet.Length then
            failwith "Enclosing type parameter count is greater than the entity's type parameter count."

        tyParsToSet
        |> ImArray.iter (fun x ->
            OlyAssert.Equal(TypeParameterKind.Type, x.Kind)
        )
#endif
        match pass with
        | Pass0
        | Pass4 ->
            tyParsHole.contents <- tyParsToSet
        | _ ->
            failwith "Invalid pass."

    member _.NamespaceAddEntity(entToAdd: EntitySymbol) =
        if ent.IsNamespace then
            entsHole.Add(entToAdd)
        else
            failwith "Expected namespace."

    static member Create(containingAsmOpt, enclosing, name, flags, kind) =
        // TODO: Change EntitySymbol to allow setting properties instead of using a bunch of ref cells.
        let attrsHole = ref ImArray.empty
        let funcsHole = ref ImArray.empty
        let fieldsHole = ref ImArray.empty
        let propsHole = ref ImArray.empty
        let patsHole = ref ImArray.empty
        let tyParsHole = ref ImArray.empty
        let extendsHole = ref ImArray.empty
        let implementsHole = ref ImArray.empty
        let entsHole = ResizeArray()
        let runtimeTyOptHole = ref None

        let ent = EntityDefinitionSymbol(containingAsmOpt, enclosing, attrsHole, name, flags, kind, tyParsHole, funcsHole, fieldsHole, propsHole, patsHole, extendsHole, implementsHole, runtimeTyOptHole, entsHole)
        EntitySymbolBuilder(ent, ImArray.empty, ImArray.empty, attrsHole, funcsHole, fieldsHole, propsHole, patsHole, tyParsHole, extendsHole, implementsHole, runtimeTyOptHole, entsHole)

    static member CreateModule(containingAsmOpt, enclosing, flags, name) =
        EntitySymbolBuilder.Create(containingAsmOpt, enclosing, name, flags ||| EntityFlags.Abstract ||| EntityFlags.Final, EntityKind.Module)

    static member CreateNamespace(enclosing: EnclosingSymbol, name) =
        if not enclosing.IsNamespace then
            failwith "Expected a namespace entity."
        EntitySymbolBuilder.Create(None, enclosing, name, EntityFlags.None, EntityKind.Namespace)

    static member CreateClosure(containingAsmOpt, enclosing: EnclosingSymbol, name, flags) =
        EntitySymbolBuilder.Create(containingAsmOpt, enclosing, name, flags, EntityKind.Closure)

    static member CreateAnonymousShape(enclosing, asm) =
        match enclosing with
        | EnclosingSymbol.RootNamespace
        | EnclosingSymbol.Entity _
        | EnclosingSymbol.Local -> ()
        | _ -> failwith "Invalid enclosing for anonymous shape."

        EntitySymbolBuilder.Create(Some asm, enclosing, "", EntityFlags.Abstract, EntityKind.Shape)

[<Sealed>]
type NamespaceBuilder private (entBuilder: EntitySymbolBuilder) =

    let set = System.Collections.Concurrent.ConcurrentDictionary()

    member _.Entity = entBuilder.Entity

    member _.EntityBuilder = entBuilder

    member _.AddEntity(ent: EntitySymbol, tyParCount: int32) =
        // Prevent duplicates
        if set.ContainsKey(ent.Name, tyParCount) then ()
        else
            entBuilder.NamespaceAddEntity(ent)
            set.[(ent.Name, tyParCount)] <- ()

    static member Create(enclosing, name) =
        let entBuilder = EntitySymbolBuilder.CreateNamespace(enclosing, name)
        NamespaceBuilder(entBuilder)
