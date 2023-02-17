﻿namespace rec Oly.Compiler.Syntax

open System
open System.Text
open System.Threading
open System.Runtime.CompilerServices
open System.Diagnostics
open Oly.Core
open Oly.Compiler.Text
open Oly.Compiler.Syntax.Internal
open Oly.Compiler.Syntax.Internal.Lexer
open Oly.Compiler.Syntax.Internal.Parser
open Oly.Compiler.Syntax.Internal.SyntaxErrors

[<Sealed>]
type OlySourceLocation private (textSpan: OlyTextSpan, syntaxTree: OlySyntaxTree) =
    
    member _.TextSpan = textSpan

    member _.SyntaxTree = syntaxTree

    member _.GetTextRange(ct: CancellationToken) =
        syntaxTree.GetSourceText(ct).GetTextRange(textSpan)

    internal new (node: OlySyntaxNode) =
        OlySourceLocation(node.TextSpan, node.Tree)

    static member Create(textSpan: OlyTextSpan, syntaxTree: OlySyntaxTree) =
        OlySourceLocation(textSpan, syntaxTree)
        

[<RequireQualifiedAccess>]
type OlyDiagnosticKind =
    | Syntactic
    | Semantic

[<AbstractClass>]
type OlyDiagnostic internal () =

    abstract Message : string

    abstract IsError : bool

    abstract IsWarning : bool

    abstract IsInformational : bool

    abstract TextSpan : OlyTextSpan

    abstract SyntaxTree : OlySyntaxTree option

    abstract OffsideAmount : int

    abstract Code : int

    abstract Kind : OlyDiagnosticKind

    override this.ToString() = 
        this.ToString(CancellationToken.None)

    member private this.ToString(ct) =
        let helperText = this.GetHelperText(ct)
        if String.IsNullOrWhiteSpace helperText then
            this.Message
        else

        $"{this.Message}
-----------------------------
{helperText}
        "

    member this.GetHelperText(?ct: CancellationToken) =
        match this.SyntaxTree with
        | Some syntaxTree ->
            let ct = defaultArg ct CancellationToken.None

            ct.ThrowIfCancellationRequested()

            let textSpan = this.TextSpan

            let sourceText = syntaxTree.GetSourceText(ct)
            let sourceLine = sourceText.Lines.GetLineFromPosition(textSpan.Start)
            let sourceLineTest = sourceText.Lines.GetLineFromPosition(textSpan.End)

            if sourceLine.LineIndex = sourceLineTest.LineIndex then       
                let startI = textSpan.Start - sourceLine.TextSpan.Start

                let lineText = sourceLine.ToString()
                let locationText = 
                    let a = String.init startI (fun _ -> " ")
                    let b = String.init textSpan.Width (fun _ -> "^")
                    a + b
                lineText + "\n" + locationText
            else
                // TODO: Fix support for multi-line diagnostic location
                let startI = textSpan.Start - sourceLine.TextSpan.Start

                let lineText = sourceLine.ToString()
                let locationText = 
                    let a = String.init startI (fun _ -> " ")
                    let b = String.init textSpan.Width (fun _ -> "^")
                    a + b
                lineText + "\n" + locationText
        | _ ->
            String.Empty

    static member CreateError(message, code, syntaxNode: OlySyntaxNode) =
        let textSpan = syntaxNode.TextSpan

        { new OlyDiagnostic() with
            member _.Message = message
            member _.IsError = true
            member _.IsWarning = false
            member _.IsInformational = false
            member _.SyntaxTree = Some syntaxNode.Tree
            member _.TextSpan = textSpan
            member _.OffsideAmount = 0
            member _.Code = code
            member _.Kind = OlyDiagnosticKind.Semantic
        }

    static member CreateError(message, syntaxNode: OlySyntaxNode) =
        OlyDiagnostic.CreateError(message, -1, syntaxNode)

    static member CreateError(message, location: OlySourceLocation) =
        let syntaxTree = Some location.SyntaxTree
        let textSpan = location.TextSpan
        { new OlyDiagnostic() with
            member _.Message = message
            member _.IsError = true
            member _.IsWarning = false
            member _.IsInformational = false
            member _.SyntaxTree = syntaxTree
            member _.TextSpan = textSpan
            member _.OffsideAmount = 0
            member _.Code = -1
            member _.Kind = OlyDiagnosticKind.Semantic
        }

    static member CreateError(message, code, location: OlySourceLocation) =
        let syntaxTree = Some location.SyntaxTree
        let textSpan = location.TextSpan
        { new OlyDiagnostic() with
            member _.Message = message
            member _.IsError = true
            member _.IsWarning = false
            member _.IsInformational = false
            member _.SyntaxTree = syntaxTree
            member _.TextSpan = textSpan
            member _.OffsideAmount = 0
            member _.Code = code
            member _.Kind = OlyDiagnosticKind.Semantic
        }

    static member CreateSyntacticWarning(message, code, location: OlySourceLocation) =
        let syntaxTree = Some location.SyntaxTree
        let textSpan = location.TextSpan
        { new OlyDiagnostic() with
            member _.Message = message
            member _.IsError = false
            member _.IsWarning = true
            member _.IsInformational = false
            member _.SyntaxTree = syntaxTree
            member _.TextSpan = textSpan
            member _.OffsideAmount = 0
            member _.Code = code
            member _.Kind = OlyDiagnosticKind.Syntactic
        }

    static member CreateSyntacticError(message, code, location: OlySourceLocation) =
        let syntaxTree = Some location.SyntaxTree
        let textSpan = location.TextSpan
        { new OlyDiagnostic() with
            member _.Message = message
            member _.IsError = true
            member _.IsWarning = false
            member _.IsInformational = false
            member _.SyntaxTree = syntaxTree
            member _.TextSpan = textSpan
            member _.OffsideAmount = 0
            member _.Code = code
            member _.Kind = OlyDiagnosticKind.Syntactic
        }

    static member CreateError(message) =
        { new OlyDiagnostic() with
            member _.Message = message
            member _.IsError = true
            member _.IsWarning = false
            member _.IsInformational = false
            member _.SyntaxTree = None
            member _.TextSpan = OlyTextSpan()
            member _.OffsideAmount = 0
            member _.Code = -1
            member _.Kind = OlyDiagnosticKind.Semantic
        }

    static member PrepareForOutput(diags: OlyDiagnostic imarray, ct: CancellationToken) =
        if diags.IsEmpty then
            String.Empty
        else
            let s = StringBuilder(diags.Length)
            diags
            |> ImArray.iter (fun diag ->
                s.AppendLine(diag.ToString(ct)) |> ignore
            )
            s.ToString()

[<Sealed>]
type internal OlyDiagnosticSyntaxInternal (msg, code, severity, textSpan, offsidesAmount, syntaxTreeOpt) =
    inherit OlyDiagnostic()
    
    override _.Message = msg

    override _.IsError = severity = DiagnosticSeverity.Error

    override _.IsWarning = severity = DiagnosticSeverity.Warning

    override _.IsInformational = false

    override _.TextSpan = textSpan

    override _.SyntaxTree = syntaxTreeOpt

    override _.OffsideAmount = offsidesAmount

    override _.Code = code

    override _.Kind = OlyDiagnosticKind.Syntactic

    new(diagnostic: DiagnosticSyntax, syntaxNode: OlySyntaxNode) =
        let textSpan =
            if diagnostic.CanShrinkErrorRangeToEnd then
                OlyTextSpan.Create(syntaxNode.TextSpan.End, 0)
            else
                syntaxNode.TextSpan
        OlyDiagnosticSyntaxInternal(
            diagnostic.Message,
            diagnostic.Code,
            diagnostic.Severity,
            textSpan,
            diagnostic.OffsidesAmount,
            Some syntaxNode.Tree
        )

[<Sealed>]
type OlyDiagnosticLogger private () =

    let mutable hasErrors = false
    let queue = System.Collections.Concurrent.ConcurrentQueue()

    member _.Error(text: string, code: int, node: OlySyntaxNode) =
        hasErrors <- true
        queue.Enqueue(OlyDiagnostic.CreateError(text, code, node))

    member _.Error(text: string, code: int, startOffset: int, width: int, node: OlySyntaxNode) =
        hasErrors <- true
        let textSpan = node.TextSpan
        let textSpan = OlyTextSpan.Create(textSpan.Start + startOffset, width)
        queue.Enqueue(OlyDiagnostic.CreateError(text, code, OlySourceLocation.Create(textSpan, node.Tree)))

    member _.HasAnyErrors = hasErrors

    member _.AddDiagnostic(diag) =
        queue.Enqueue(diag)

    member _.GetDiagnostics() = 
        if queue.Count = 0 then
            ImArray.empty
        else
            queue.ToArray() |> ImArray.ofSeq

    static member Create() = OlyDiagnosticLogger()

[<AllowNullLiteral;AbstractClass;NoComparison>]
type OlySyntaxNode internal (tree: OlySyntaxTree, parent: OlySyntaxNode, internalNode: ISyntaxNode) =
    
    let mutable leadingTriviaWidth = -1

    member _.Tree = tree

    /// Can be 'null'.
    member _.Parent = parent
    member _.HasParent = isNull(parent)

    member this.GetDiagnostics(ct: CancellationToken): OlyDiagnostic imarray =
        tree.GetInternal(ct).GetDiagnostics(internalNode) 
        |> ImArray.map (fun x -> OlyDiagnosticSyntaxInternal(x, this) :> OlyDiagnostic)

    member this.GetText(ct) = tree.GetSourceText(ct).GetSubText(this.TextSpan)

    member this.GetFullText(ct) = tree.GetSourceText(ct).GetSubText(this.FullTextSpan)

    member this.GetLeadingTriviaWidth() =
        if leadingTriviaWidth = -1 then
            leadingTriviaWidth <- internalNode.GetLeadingTriviaWidth()
        leadingTriviaWidth

    member internal _.InternalNode = internalNode

    override this.Equals(o) =
        match o with
        | :? OlySyntaxNode as o ->
            internalNode = o.InternalNode
        | _ ->
            false

    override _.GetHashCode() =
        internalNode.GetHashCode()

    static member internal TryGetFirstToken(children: OlySyntaxNode imarray) =
        let mutable node: OlySyntaxNode = null

        let mutable i = 0
        while isNull(node) && i < children.Length do
            let child = children[i]

            if child.InternalNode.IsToken && not child.InternalNode.IsTerminal then
                node <- child
            else
                node <- OlySyntaxNode.TryGetFirstToken(child.Children)

            i <- i + 1

        node

    member this.TryGetFirstToken(ct: CancellationToken) =
        let rec loop (node: OlySyntaxNode) =
            ct.ThrowIfCancellationRequested()
            if node.InternalNode.IsToken && not node.InternalNode.IsTerminal then
                Some node
            else
                node.Children
                |> ImArray.tryPick loop

        this.Children
        |> ImArray.tryPick loop

    abstract Children : OlySyntaxNode imarray
    default _.Children = ImArray.empty

    abstract TextSpan : OlyTextSpan

    abstract FullTextSpan : OlyTextSpan

[<Sealed;NoComparison>]
type OlySyntaxToken internal (tree, start: int, parent: OlySyntaxNode, internalNode: SyntaxToken) =
    inherit OlySyntaxNode(tree, parent, internalNode)

    let mutable children = ImArray.empty

    override this.TextSpan =
        let offset = this.GetLeadingTriviaWidth()
        OlyTextSpan.Create(start + offset, this.FullTextSpan.Width - offset)

    override _.FullTextSpan = 
        OlyTextSpan.Create(start, internalNode.FullWidth)

    override this.Children =
        if children.IsEmpty && (internalNode :> ISyntaxNode).SlotCount > 0 then
            children <-
                ImArray.init
                    (internalNode :> ISyntaxNode).SlotCount
                    (fun i -> 
                        let node = (internalNode :> ISyntaxNode).GetSlot(i) :?> SyntaxToken
                        OlySyntaxToken(tree, start, this, node) :> OlySyntaxNode
                    )
        children

    member internal _.Internal = internalNode

[<NoEquality;NoComparison>]
type OlyCompilationUnitConfiguration =
    private {
        target: (OlyTextSpan * string) option
        loads: (OlyTextSpan * OlyPath) imarray
        references: (OlyTextSpan * OlyPath) imarray
        packages: (OlyTextSpan * string) imarray
        directiveDiagnostics: OlyDiagnostic imarray
        isLibrary: bool
    }

    member this.Target = this.target

    member this.References = this.references

    member this.Loads = this.loads

    member this.Packages = this.packages

    member this.IsLibrary = this.isLibrary

[<NoEquality;NoComparison;RequireQualifiedAccess>]
type OlyParsingOptions =
    {
        CompilationUnitConfigurationEnabled: bool
        AnonymousModuleDefinitionAllowed: bool
        ConditionalDefines: string imarray
    }

    static member Default =
        {
            CompilationUnitConfigurationEnabled = false
            AnonymousModuleDefinitionAllowed = false
            ConditionalDefines = ImArray.empty
        }

    static member AreEqual(options1, options2) =
        options1.CompilationUnitConfigurationEnabled = options2.CompilationUnitConfigurationEnabled &&
        options1.AnonymousModuleDefinitionAllowed = options2.AnonymousModuleDefinitionAllowed &&
        (options1.ConditionalDefines, options2.ConditionalDefines)
        ||> ImArray.forall2 (fun x y -> x = y)

[<AbstractClass>] 
type OlySyntaxTree internal (path: OlyPath, getText: CacheValue<IOlySourceText>, version: uint64, parsingOptions: OlyParsingOptions) as this =

    let dummyNode =
        CacheValue(fun _ ->
            { new OlySyntaxNode(this, null, syntaxTerminal) with
                override _.Children = ImArray.empty
                override _.TextSpan = OlyTextSpan()
                override _.FullTextSpan = OlyTextSpan() 
            }
        )

    let mutable lexerDiags = ImArray.empty

    let getSyntaxTree =
        WeakCacheValue(fun ct ->
            let lexer = Lexer.create parsingOptions.ConditionalDefines (getText.GetValue(ct))
            let internalTree = Parse(path, lexer, ct)

            lexerDiags <-
                lexer.GetCurrentDiagnostics()
                |> ImArray.map (fun (startPos, endPos, msg, isError, code) ->
                    let location = 
                        OlySourceLocation.Create(
                            OlyTextSpan.CreateWithEnd(startPos, endPos),
                            this
                        )
                    if isError then
                        OlyDiagnostic.CreateSyntacticError(msg, code, location)
                    else
                        OlyDiagnostic.CreateSyntacticWarning(msg, code, location)
                )

            internalTree
        )

    let getDiagnostics =
        CacheValue(fun ct ->
            let _ = getSyntaxTree.GetValue(ct)
            let root = this.GetRoot(ct)
            
            let diags = ImArray.builder ()

            let rec loop (node: OlySyntaxNode) =
                diags.AddRange(node.GetDiagnostics(ct))
                node.Children
                |> ImArray.iter loop

            loop root

            lexerDiags.AddRange(diags)
        )

    let getConfig =
        CacheValue(fun ct ->
            let lexer = Lexer.create parsingOptions.ConditionalDefines (getText.GetValue(ct))

            let directives = Lexer.getDirectives lexer ct
            let diags = ImArray.builder()

            let checkConfigDirective textSpan =               
                if not parsingOptions.CompilationUnitConfigurationEnabled then
                    diags.Add(OlyDiagnostic.CreateError($"Compilation unit configuration is not enabled.", OlySourceLocation.Create(textSpan, this)))

            let targets =
                directives
                |> ImArray.choose(fun (startPos, endPos, rawToken) ->
                    match rawToken with
                    | Directive(_, identToken, _, valueToken) ->
                        let textSpan = OlyTextSpan.Create(startPos, endPos - startPos)
                        match identToken.Text with
                        | "target" ->
                            checkConfigDirective textSpan
                            Some(textSpan, valueToken.ValueText)
                        | _ -> 
                            None
                    | _ ->
                        None
                )

            let target =
                if targets.IsEmpty then
                    None
                elif targets.Length > 1 then
                    let textSpan, _ = targets.[0]
                    diags.Add(OlyDiagnostic.CreateError($"Only one target may be specified.", OlySourceLocation.Create(textSpan, this)))
                    None
                else
                    Some targets.[0]

            let loads =
                directives
                |> ImArray.choose(fun (startPos, endPos, rawToken) ->
                    match rawToken with
                    | Directive(_, identToken, _, valueToken) ->
                        let textSpan = OlyTextSpan.Create(startPos, endPos - startPos)
                        match identToken.Text with
                        | "load" -> 
                            checkConfigDirective textSpan
                            Some(textSpan, OlyPath.Create(valueToken.ValueText))
                        | _ -> 
                            None
                    | _ ->
                        None
                )

            let references =
                directives
                |> ImArray.choose(fun (startPos, endPos, rawToken) ->
                    match rawToken with
                    | Directive(_, identToken, _, valueToken) ->
                        let textSpan = OlyTextSpan.Create(startPos, endPos - startPos)
                        match identToken.Text with
                        | "reference" -> 
                            checkConfigDirective textSpan
                            Some(textSpan, OlyPath.Create(valueToken.ValueText))
                        | _ -> 
                            None
                    | _ ->
                        None
                )

            let packages =
                directives
                |> ImArray.choose(fun (startPos, endPos, rawToken) ->
                    match rawToken with
                    | Directive(_, identToken, _, valueToken) ->
                        let textSpan = OlyTextSpan.Create(startPos, endPos - startPos)
                        match identToken.Text with
                        | "package" -> 
                            checkConfigDirective textSpan
                            Some(textSpan, valueToken.ValueText)
                        | _ -> 
                            None
                    | _ ->
                        None
                )

            let isLibrary =
                directives
                |> ImArray.exists(fun (startPos, endPos, rawToken) ->
                    match rawToken with
                    | DirectiveFlag(_, identToken) ->
                        let textSpan = OlyTextSpan.Create(startPos, endPos - startPos)
                        match identToken.Text with
                        | "library" -> 
                            checkConfigDirective textSpan
                            true
                        | _ -> 
                            false
                    | _ ->
                        false
                )

            directives
            |> ImArray.iter(fun (startPos, endPos, rawToken) ->
                match rawToken with
                | Directive(hashToken, identToken, _, _) ->
                    let textSpan = OlyTextSpan.Create(startPos, endPos - startPos)
                    match identToken.Text with
                    | "reference"
                    | "target"
                    | "load"
                    | "package" -> ()
                    | _ ->
                        diags.Add(OlyDiagnostic.CreateError($"The directive '{ (hashToken.ValueText + identToken.ValueText) }' is invalid.", OlySourceLocation.Create(textSpan, this)))
                | _ ->
                    ()
            )

            { target = target; references = references; loads = loads; packages = packages; directiveDiagnostics = diags.ToImmutable(); isLibrary = isLibrary }
        )

    abstract WithPath : OlyPath -> OlySyntaxTree
    abstract GetRoot : ct: CancellationToken -> OlySyntaxNode
    abstract ApplySourceText : IOlySourceText -> OlySyntaxTree

    member _.Path = path
    member _.Version = version

    member internal _.GetInternal(ct): SyntaxTree = getSyntaxTree.GetValue(ct)

    member _.DummyNode = dummyNode.GetValue(CancellationToken.None)
    member _.ParsingOptions = parsingOptions

    member _.GetDiagnostics(ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()

        let directiveDiags = getConfig.GetValue(ct).directiveDiagnostics
        directiveDiags.AddRange(getDiagnostics.GetValue(ct))

    member _.GetSourceText(ct: CancellationToken): IOlySourceText = 
        ct.ThrowIfCancellationRequested()

        getText.GetValue(ct)

    member _.GetCompilationUnitConfiguration(?ct) =
        let ct = defaultArg ct CancellationToken.None
        getConfig.GetValue(ct)

    member this.HasErrors =
        let diags = this.GetDiagnostics(CancellationToken.None)
        diags |> ImArray.exists (fun x -> x.IsError)

    internal new(path, getText: CancellationToken -> IOlySourceText, version, options) =
        let getText = CacheValue(getText)
        OlySyntaxTree(path, getText, version, options)