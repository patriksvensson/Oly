module Tests

open System
open System.Threading
open Xunit
open Oly.Core
open Oly.Compiler
open Oly.Compiler.Text
open Oly.Compiler.Syntax
open Oly.Compiler.Workspace
open Oly.Compiler.Workspace.Extensions

let createWorkspace() =
    OlyWorkspace.Create([Oly.Runtime.Target.Interpreter.InterpreterTarget()])

let createWorkspaceWith(f) =
    let rs =
        {
            new IOlyWorkspaceResourceService with
        
                member _.LoadSourceText(filePath) =
                    f filePath
        
                member _.GetTimeStamp(filePath) = DateTime()
        
                member _.FindSubPaths(dirPath) =
                    ImArray.empty
        
                member _.LoadProjectConfigurationAsync(_projectFilePath: OlyPath, ct: CancellationToken) =
                    backgroundTask {
                        ct.ThrowIfCancellationRequested()
                        return OlyProjectConfiguration(String.Empty, ImArray.empty, false)
                    }
        }
    OlyWorkspace.Create([Oly.Runtime.Target.Interpreter.InterpreterTarget()], rs)

let createProject src (workspace: OlyWorkspace) =
    let path = OlyPath.Create "olytest.olyx"
    workspace.UpdateDocument(path, OlySourceText.Create(src), CancellationToken.None)
    workspace.GetDocumentsAsync(path, CancellationToken.None).Result[0].Project  

[<System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)>]
let createProjectWeakReference src workspace =
    let proj = createProject src workspace
    let comp = proj.Compilation
    let syntaxTree = proj.Documents[0].SyntaxTree
    WeakReference<OlyProject>(proj), WeakReference<OlyCompilation>(comp), WeakReference<OlySyntaxTree>(syntaxTree)

let createDocument src (workspace: OlyWorkspace) =
#if DEBUG
    let isDebuggable = true
#else
    let isDebuggable = false
#endif
    let projOptions = OlyProjectConfiguration("olytest", ImArray.empty, isDebuggable)
    let sol, proj = workspace.Solution.CreateProject(OlyPath.Create "olytest", projOptions, "dotnet", OlyTargetInfo("net7", OlyOutputKind.Executable, Some "System.ValueType", Some "System.Enum"), CancellationToken.None)
    let syntaxTree = OlySyntaxTree.Parse(OlyPath.Create "olytest", (fun _ -> OlySourceText.Create(src)))
    let sol, proj, doc = sol.UpdateDocument(proj.Path, OlyPath.Create "olytest", syntaxTree, ImArray.empty)
    doc

let updateDocument path src (workspace: OlyWorkspace) =
    workspace.UpdateDocumentAsync(path, OlySourceText.Create(src), CancellationToken.None).Result
    |> ignore

let shouldCompile (proj: OlyProject) =
    let diags = proj.Compilation.GetDiagnostics(CancellationToken.None)
    Assert.Empty(diags)

let getDocumentWithCursor (srcWithCursor: string) =
    let cursorPosition = srcWithCursor.IndexOf("~^~")
    let src = srcWithCursor.Replace("~^~", "")
    let doc =
        createWorkspace()
        |> createDocument src
    (cursorPosition, doc)

let getCompletionLabels (srcWithCursor: string) =
    let (cursorPosition, doc) = getDocumentWithCursor srcWithCursor

    let completionLabels = 
        doc.GetCompletions(cursorPosition, CancellationToken.None)
        |> Seq.groupBy (fun x -> x.Label)
        |> Seq.map (fun (label, xs) -> System.Collections.Generic.KeyValuePair(label, xs))
       
    let completionLabels = 
        System.Collections.Generic.Dictionary(completionLabels)
        |> System.Collections.ObjectModel.ReadOnlyDictionary
        :> System.Collections.Generic.IReadOnlyDictionary<_, _>
    completionLabels

let containsCompletionLabelsByCursor (expectedCompletionLabels: string seq) (srcWithCursor: string) =
    let completionLabels = getCompletionLabels srcWithCursor
    expectedCompletionLabels
    |> Seq.iter (fun expected ->
        Assert.Contains(expected, completionLabels)
        |> ignore
    )

let doesNotContainCompletionLabelsByCursor (expectedCompletionLabels: string seq) (srcWithCursor: string) =
    let completionLabels = getCompletionLabels srcWithCursor
    expectedCompletionLabels
    |> Seq.iter (fun expected ->
        Assert.DoesNotContain(expected, completionLabels)
        |> ignore
    )

let containsOnlyCompletionLabelsByCursor (expectedCompletionLabels: string seq) (srcWithCursor: string) =
    let completionLabels = getCompletionLabels srcWithCursor
    Assert.Equal(Seq.length expectedCompletionLabels, completionLabels.Count)
    expectedCompletionLabels
    |> Seq.forall (fun expected -> completionLabels.ContainsKey(expected))
    |> Assert.True

let getFunctionCallInfoByCursor (srcWithCursor: string) =
    let (cursorPosition, doc) = getDocumentWithCursor srcWithCursor
    if cursorPosition = -1 then
        failwith "Cursor position cannot be -1."
    match doc.TryFindFunctionCallInfo(cursorPosition, CancellationToken.None) with
    | Some x -> x
    | _ -> failwith "Unable to find function call symbol."

let getFunctionCallSymbolByCursor (srcWithCursor: string) =
    (getFunctionCallInfoByCursor srcWithCursor).Function

[<Fact>]
let ``Simple workspace with hello world project should compile`` () =
    let src =
        """
#target "i: default"

module Test

#[intrinsic("print")]
print(__oly_object): ()

main(): () =
    print("Hello World!")
        """
    createWorkspace()
    |> createProject src
    |> shouldCompile

[<Fact>]
let ``By cursor, get completions of local variable 'x'`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
    ~^~
    """
    |> containsCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions of local variable 'x' 2`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
    let y = 1
    ~^~
    """
    |> containsCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions of local variable 'x' 3`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
    ~^~
    let y = 1
    """
    |> containsCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions of local variable 'x' 4`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
    ~^~
    ()
    """
    |> containsCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions of local variable 'x' 5`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
~^~
    """
    |> doesNotContainCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions of local variable 'x' 6`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
 ~^~
    """
    |> doesNotContainCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions of local variable 'x' 7`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
  ~^~
    """
    |> doesNotContainCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions of local variable 'x' 8`` () =
    """
#target "i: default"

module Test

main(): () =
    let x = 1
   ~^~
    """
    |> doesNotContainCompletionLabelsByCursor ["x"]

[<Fact>]
let ``By cursor, get completions from a local variable`` () =
    """
#target "i: default"

struct Test =
    
    mutable X: __oly_int32 = 3

main(): () =
    let t = Test()
    t.~^~
    """
    |> containsCompletionLabelsByCursor ["X"]

[<Fact>]
let ``By cursor, get completions from a byref local variable`` () =
    """
#target "i: default"

#[intrinsic("by_ref_read_write")]
alias byref<T>

#[intrinsic("address_of")]
(&)(T): byref<T>

struct Test =
    
    mutable X: __oly_int32 = 3

main(): () =
    let mutable t = Test()
    let x: byref<Test> = &t
    x.~^~
    """
    |> containsCompletionLabelsByCursor ["X"]

[<Fact>]
let ``By cursor, get completions from a byref local variable 2`` () =
    """
#target "i: default"

#[intrinsic("by_ref_read_write")]
alias byref<T>

#[intrinsic("address_of")]
(&)<T>(T): byref<T>

struct Test =
    
    mutable X: __oly_int32 = 3

main(): () =
    let mutable t = Test()
    let x = &t
    x.~^~
    """
    |> containsCompletionLabelsByCursor ["X"]

[<Fact>]
let ``By cursor, get completions from a namespace`` () =
    """
namespace TestNamespace

#target "i: default"

struct Test =
    
    mutable X: __oly_int32 = 3

main(): () =
    let t = TestNamespace.~^~
    """
    |> containsCompletionLabelsByCursor ["Test"]

[<Fact>]
let ``By cursor, get completions from a cast`` () =
    """
#target "i: default"

#[intrinsic("print")]
print(__oly_object): ()

interface IA =

    B(): ()

    default A(): () =
        this.B()

struct Test =
    implements IA
    
    mutable X: __oly_int32 = 3

    mutable B(): () =
        this.X <- 5

main(): () =
    let x = Test()
    (x: IA).~^~
    print(x.X)
    """
    |> containsCompletionLabelsByCursor ["A";"B"]

let clearSolution (workspace: OlyWorkspace) =
    workspace.ClearSolutionAsync(CancellationToken.None).Result

let mutable workspaceGC_stub = Unchecked.defaultof<OlyWorkspace>

[<Fact>]
let ``Project should be GC'ed``() =
    let src =
        """
#target "i: default"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
    
main(): () =
    print("Hello World!")
        """
    let workspace = createWorkspace()
    workspaceGC_stub <- workspace
    let projWeak, compWeak, treeWeak = createProjectWeakReference src workspace

    let mutable proj = Unchecked.defaultof<_>
    let mutable comp = Unchecked.defaultof<_>
    let mutable tree = Unchecked.defaultof<_>

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.True(projWeak.TryGetTarget(&proj))
    Assert.True(compWeak.TryGetTarget(&comp))
    Assert.True(treeWeak.TryGetTarget(&tree))

    proj <- Unchecked.defaultof<_>
    comp <- Unchecked.defaultof<_>
    tree <- Unchecked.defaultof<_>

    clearSolution workspace

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.False(projWeak.TryGetTarget(&proj))
    Assert.False(compWeak.TryGetTarget(&comp))
    Assert.False(treeWeak.TryGetTarget(&tree))

    workspaceGC_stub <- Unchecked.defaultof<_>

let clearSolution2 (path: OlyPath) (workspace: OlyWorkspace) =
    let mutable currentDocs = workspace.GetDocumentsAsync(path, CancellationToken.None).Result
    Assert.True(currentDocs.Length = 1)
    workspace.ClearSolutionAsync(CancellationToken.None).Result
    currentDocs <- workspace.GetDocumentsAsync(path, CancellationToken.None).Result
    Assert.True(currentDocs.Length = 0)

[<Fact>]
let ``Project should be GC'ed 2``() =
    let src =
        """
#target "i: default"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
    
main(): () =
    print("Hello World!")
        """
    let workspace = createWorkspace()
    workspaceGC_stub <- workspace
    let projWeak, compWeak, treeWeak = createProjectWeakReference src workspace

    let mutable proj = Unchecked.defaultof<_>
    let mutable comp = Unchecked.defaultof<_>
    let mutable tree = Unchecked.defaultof<_>

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.True(projWeak.TryGetTarget(&proj))
    Assert.True(compWeak.TryGetTarget(&comp))
    Assert.True(treeWeak.TryGetTarget(&tree))

    let path = proj.Path

    proj <- Unchecked.defaultof<_>
    comp <- Unchecked.defaultof<_>
    tree <- Unchecked.defaultof<_>

    clearSolution2 path workspace

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.False(projWeak.TryGetTarget(&proj))
    Assert.False(compWeak.TryGetTarget(&comp))
    Assert.False(treeWeak.TryGetTarget(&tree))

    workspaceGC_stub <- Unchecked.defaultof<_>

[<Fact>]
let ``Project should be GC'ed 3``() =
    let src =
        """
#target "i: default"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
    
main(): () =
    print("Hello World!")
        """
    let workspace = createWorkspace()
    workspaceGC_stub <- workspace
    let projWeak, compWeak, treeWeak = createProjectWeakReference src workspace

    let mutable proj = Unchecked.defaultof<_>
    let mutable comp = Unchecked.defaultof<_>
    let mutable tree = Unchecked.defaultof<_>

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.True(projWeak.TryGetTarget(&proj))
    Assert.True(compWeak.TryGetTarget(&comp))
    Assert.True(treeWeak.TryGetTarget(&tree))

    let path = proj.Path

    proj <- Unchecked.defaultof<_>
    comp <- Unchecked.defaultof<_>
    tree <- Unchecked.defaultof<_>

    updateDocument path (src + " ") workspace

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.False(projWeak.TryGetTarget(&proj))
    Assert.False(compWeak.TryGetTarget(&comp))
    Assert.False(treeWeak.TryGetTarget(&tree))

    workspaceGC_stub <- Unchecked.defaultof<_>

[<Fact>]
let ``Project should not be GC'ed as the source text is the same``() =
    let src =
        """
#target "i: default"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
    
main(): () =
    print("Hello World!")
        """
    let workspace = createWorkspace()
    workspaceGC_stub <- workspace
    let projWeak, compWeak, treeWeak = createProjectWeakReference src workspace

    let mutable proj = Unchecked.defaultof<_>
    let mutable comp = Unchecked.defaultof<_>
    let mutable tree = Unchecked.defaultof<_>

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.True(projWeak.TryGetTarget(&proj), "proj - before")
    Assert.True(compWeak.TryGetTarget(&comp), "comp - before")
    Assert.True(treeWeak.TryGetTarget(&tree), "tree - before")

    let path = proj.Path

    proj <- Unchecked.defaultof<_>
    comp <- Unchecked.defaultof<_>
    tree <- Unchecked.defaultof<_>

    updateDocument path src workspace

    for _i = 1 to 10 do
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()

    Assert.True(projWeak.TryGetTarget(&proj), "proj - after")
    Assert.True(compWeak.TryGetTarget(&comp), "comp - after")
    Assert.True(treeWeak.TryGetTarget(&tree), "tree - after")

    workspaceGC_stub <- Unchecked.defaultof<_>

[<Fact>]
let ``Project should work with multiple dots in the name``() =
    let src =
        """
#target "i: default"

#load "../fakepath/*.oly"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
    
main(): () =
    print("Hello World!")
        """
    let workspace = createWorkspace()
    let path = OlyPath.Create("Oly.Runtime.Target.Example.olyx")
    workspace.UpdateDocument(path, OlySourceText.Create(src), CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path, CancellationToken.None).Result[0].Project
    Assert.Equal("Oly.Runtime.Target.Example", proj.Name)
    Assert.Equal("Oly.Runtime.Target.Example", proj.Compilation.AssemblyName)
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.NotEqual(0, symbols.Length)

[<Fact>]
let ``Project should fail when trying to reference a Oly file``() =
    let src =
        """
#target "i: default"

#reference "fake.oly"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
    
main(): () =
    print("Hello World!")
        """
    let workspace = createWorkspace()
    let path = OlyPath.Create("Test.olyx")
    workspace.UpdateDocument(path, OlySourceText.Create(src), CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path, CancellationToken.None).Result[0].Project
    
    let diags = proj.Compilation.GetDiagnostics(CancellationToken.None)
    Assert.Equal(1, diags.Length)
    Assert.Equal("Cannot reference Oly file(s) 'fake.oly'. Use '#load' instead.", diags[0].Message)

[<Fact>]
let ``Project should fail when trying to reference a Oly file 2``() =
    let src =
        """
#target "i: default"

#reference "*.oly"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
    
main(): () =
    print("Hello World!")
        """
    let workspace = createWorkspace()
    let path = OlyPath.Create("Test.olyx")
    workspace.UpdateDocument(path, OlySourceText.Create(src), CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path, CancellationToken.None).Result[0].Project
    
    let diags = proj.Compilation.GetDiagnostics(CancellationToken.None)
    Assert.Equal(1, diags.Length)
    Assert.Equal("Cannot reference Oly file(s) '*.oly'. Use '#load' instead.", diags[0].Message)

[<Fact>]
let ``Project reference another project``() =
    let src1 =
        """
#target "i: default"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

open static Test

main(): () =
    print("Hello World!")
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

[<Fact>]
let ``Project reference another project 2``() =
    let src1 =
        """
#target "i: default"

module Test
    
#[intrinsic("print")]
print(__oly_object): ()
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

main(): () =
    Test.print("Hello World!")
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

[<Fact>]
let ``Project reference another project 3``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test
    
#[intrinsic("print")]
print(__oly_object): ()
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

main(): () =
    print("Hello World!")
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)


[<Fact>]
let ``Project reference another project 4``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

class TestClass =

    X: __oly_int32 get = 123
    
#[intrinsic("print")]
print(__oly_object): ()
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

main(): () =
    let x = TestClass()
    print(x.X)
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

[<Fact>]
let ``Project reference another project 5``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

class TestClass =

    X: __oly_int32 get = 123

    static op_Multiply(x: TestClass, y: TestClass): TestClass = x
    
#[intrinsic("print")]
print(__oly_object): ()

testShape<T>(x: T): () where T: { X: __oly_int32 get } =
    ()

shape AddShape<T1, T2, T3> =
    
    static op_Addition(T1, T2): T3

shape MultiplyShape<T1, T2, T3> =

    static op_Multiply(T1, T2): T3

(+)<T1, T2, T3>(x: T1, y: T2): T3 where T1: AddShape<T1, T2, T3> = T1.op_Addition(x, y)
(*)<T1, T2, T3>(x: T1, y: T2): T3 where T1: MultiplyShape<T1, T2, T3> = T1.op_Multiply(x, y)
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

main(): () =
    let x = TestClass()
    testShape(x)
    let result = x * x
    print(x.X)
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

[<Fact>]
let ``Project reference another project 6``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

class TestClass =

    static op_Multiply(x: TestClass, y: TestClass): TestClass = x
    
#[intrinsic("print")]
print(__oly_object): ()

(+)<T1, T2, T3>(x: T1, y: T2): T3 where T1: { static op_Addition(T1, T2): T3 } = T1.op_Addition(x, y)
(*)<T1, T2, T3>(x: T1, y: T2): T3 where T1: { static op_Multiply(T1, T2): T3 } = T1.op_Multiply(x, y)
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

main(): () =
    let x = TestClass()
    let result = x * x
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)


[<Fact>]
let ``Project reference another project 7``() =
    let src1 =
        """
#target "i: default"

#load "ui.oly"

namespace Evergreen.Client.Graphics

class Graphics
        """

    let src2 =
        """
namespace Evergreen.Client.Graphics.UI

class UI
        """

    let src3 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

open Evergreen.Client.Graphics
open Evergreen.Client.Graphics.UI

#[intrinsic("print")]
print(__oly_object): ()

main(): () =
    let g = Graphics()
    let ui = UI()
    print("passed")
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("fakepath/ui.oly")
    let path3 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let text3 = OlySourceText.Create(src3)

    // We are trying to test to make sure that namespace aggregation works properly.
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 elif OlyPath.Equals(x, path2) then text2 else failwith "Invalid path")
    workspace.UpdateDocument(path1, text1, CancellationToken.None)
    workspace.UpdateDocument(path3, text3, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path3, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

[<Fact>]
let ``Project reference another project 8``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

test(f: (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

main(): () =
    test((x: __oly_int32, y: __oly_int64) -> true)
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

[<Fact>]
let ``Project reference another project 9``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

test(f: (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let src2 =
        """
#target "i: default"

#reference "fakepath/Test.olyx"

main(): () =
    test((x: __oly_int32, y: __oly_int64) -> true)
        """

    let updatedSrc1 =
        """
#target "i: default"

#[open]
module Test

test(f: (missing_type, __oly_int64) -> __oly_bool): () =
    ()
        """

    let updatedSrc2 =
        """
#target "i: default"

#[open]
module Test

// this changed to a scoped lambda, but still should work.
test(f: scoped (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let workspace = createWorkspaceWith(fun x -> if OlyPath.Equals(x, path1) then text1 else failwith "Invalid path")
    workspace.UpdateDocument(path2, text2, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

    let task = workspace.BuildProjectAsync(path2, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

 
    // Should fail
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc1), CancellationToken.None)

    let task = workspace.BuildProjectAsync(path2, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error _ -> ()
    | _ -> OlyAssert.Fail("Expected error")

    // Should pass
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc2), CancellationToken.None)

    let task = workspace.BuildProjectAsync(path2, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

[<Fact>]
let ``Project reference another project that references another project``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

test(f: (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let src2 =
        """
#target "i: default"

#reference "Test.olyx"

#[open]
module Test2
        """

    let src3 =
        """
#target "i: default"

#reference "fakepath/Test2.olyx"

main(): () =
    test((x: __oly_int32, y: __oly_int64) -> true)
        """

    let updatedSrc1 =
        """
#target "i: default"

#[open]
module Test

test(f: (missing_type, __oly_int64) -> __oly_bool): () =
    ()
        """

    let updatedSrc2 =
        """
#target "i: default"

#[open]
module Test

// this changed to a scoped lambda, but still should work.
test(f: scoped (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("fakepath/Test2.olyx")
    let path3 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let text3 = OlySourceText.Create(src3)
    let workspace = 
        createWorkspaceWith(fun x -> 
            if OlyPath.Equals(x, path1) then 
                text1 
            elif OlyPath.Equals(x, path2) then
                text2
            else 
                failwith "Invalid path")
    workspace.UpdateDocument(path3, text3, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path3, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

 
    // Should fail
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc1), CancellationToken.None)

    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error _ -> ()
    | _ -> OlyAssert.Fail("Expected error")

    // Should pass
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc2), CancellationToken.None)

    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

[<Fact>]
let ``Project reference another project that references another project 2``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

test(f: (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let src2 =
        """
#target "i: default"

#reference "Test.olyx"

#[open]
module Test2

test2(): () =
    test((x: __oly_int32, y: __oly_int64) -> true)
        """

    let src3 =
        """
#target "i: default"

#reference "fakepath/Test2.olyx"

main(): () =
    test((x: __oly_int32, y: __oly_int64) -> true)
        """

    let updatedSrc1 =
        """
#target "i: default"

#[open]
module Test

test(f: (missing_type, __oly_int64) -> __oly_bool): () =
    ()
        """

    let updatedSrc2 =
        """
#target "i: default"

#[open]
module Test

// this changed to a scoped lambda, but still should work.
test(f: scoped (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("fakepath/Test2.olyx")
    let path3 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let text3 = OlySourceText.Create(src3)
    let workspace = 
        createWorkspaceWith(fun x -> 
            if OlyPath.Equals(x, path1) then 
                text1 
            elif OlyPath.Equals(x, path2) then
                text2
            else 
                failwith "Invalid path")
    workspace.UpdateDocument(path3, text3, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path3, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

 
    // Should fail
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc1), CancellationToken.None)

    let doc = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0]
    Assert.NotEmpty(doc.Project.Compilation.GetDiagnostics(CancellationToken.None))

    // Should fail
    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error _ -> ()
    | _ -> OlyAssert.Fail("Expected error")

    // Should pass
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc2), CancellationToken.None)

    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

[<Fact>]
let ``Project reference another project that references another project 3``() =
    let src1 =
        """
#target "i: default"

#[open]
module Test

test(f: (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let src2 =
        """
#target "i: default"

#reference "Test.olyx"

#[open]
module Test2

test2(): () =
    test((x: __oly_int32, y: __oly_int64) -> true)
        """

    let src3 =
        """
#target "i: default"

#reference "fakepath/Test2.olyx"

main(): () =
    test((x: __oly_int32, y: __oly_int64) -> true)
        """

    let updatedSrc1 =
        """
#target "i: default"

#[open]
module Test

test(f: (missing_type, __oly_int64) -> __oly_bool): () =
    ()
        """

    let updatedSrc2 =
        """
#target "i: default"

#[open]
module Test

// this changed to a scoped lambda, but still should work.
test(f: scoped (__oly_int32, __oly_int64) -> __oly_bool): () =
    ()
        """

    let path1 = OlyPath.Create("fakepath/Test.olyx")
    let path2 = OlyPath.Create("fakepath/Test2.olyx")
    let path3 = OlyPath.Create("main.olyx")
    let text1 = OlySourceText.Create(src1)
    let text2 = OlySourceText.Create(src2)
    let text3 = OlySourceText.Create(src3)
    let workspace = 
        createWorkspaceWith(fun x -> 
            if OlyPath.Equals(x, path1) then 
                text1 
            elif OlyPath.Equals(x, path2) then
                text2
            else 
                failwith "Invalid path")
    workspace.UpdateDocument(path3, text3, CancellationToken.None)
    let proj = workspace.GetDocumentsAsync(path3, CancellationToken.None).Result[0].Project
    let doc = proj.Documents[0]
    let symbols = doc.GetAllSymbols(CancellationToken.None)
    Assert.Empty(proj.Compilation.GetDiagnostics(CancellationToken.None))
    Assert.NotEqual(0, symbols.Length)

    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

 
    // Getting the document for 'path2' shouldn't screw up building. Very weird case.
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc1), CancellationToken.None)
    let _doc = workspace.GetDocumentsAsync(path2, CancellationToken.None).Result[0]

    // Should fail
    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error _ -> ()
    | _ -> OlyAssert.Fail("Expected error")

    // Should pass
    workspace.UpdateDocument(path1, OlySourceText.Create(updatedSrc2), CancellationToken.None)

    let task = workspace.BuildProjectAsync(path3, CancellationToken.None)
    let result = task.Result
    match result with
    | Result.Error(diags) -> raise(Exception(OlyDiagnostic.PrepareForOutput(diags, CancellationToken.None)))
    | _ -> ()

[<Fact>]
let ``By cursor, get completions for incomplete alias definition`` () =
    """
#target "i: default"

testFunction(): () = ()

class TestClass

alias TestAlias = ~^~
    """
    |> containsOnlyCompletionLabelsByCursor ["TestClass"]

[<Fact>]
let ``By cursor, get call syntax token`` () =
    let src =
        """
#[intrinsic("int32")]
alias int32

class C =

    GetSomething(x: int32): () = ()
    GetSomething(x: int32, y: int32): () = ()

main(): () =
    let c = C()
    c.GetSomething(~^~)
        """
    let symbol = getFunctionCallSymbolByCursor src
    Assert.True(symbol.IsFunctionGroup)
    Assert.Equal(2, symbol.AsFunctionGroup.Functions.Length)

[<Fact>]
let ``By cursor, get call syntax token 2`` () =
    let src =
        """
#[intrinsic("int32")]
alias int32

class C =

    GetSomething<T>(x: int32): () = ()
    GetSomething<T, U>(x: int32, y: int32): () = ()

main(): () =
    let c = C()
    c.GetSomething(~^~)
        """
    let symbol = getFunctionCallSymbolByCursor src
    Assert.True(symbol.IsFunctionGroup)
    Assert.Equal(2, symbol.AsFunctionGroup.Functions.Length)

[<Fact>]
let ``By cursor, get call syntax token 3`` () =
    let src =
        """
#[intrinsic("int32")]
alias int32

class C =

    GetSomething<T>(x: int32): () = ()
    GetSomething<T, U>(x: int32, y: int32): () = ()

main(): () =
    let c = C()
    c.GetSomething~^~()
        """
    let symbol = getFunctionCallSymbolByCursor src
    Assert.True(symbol.IsFunctionGroup)
    Assert.Equal(2, symbol.AsFunctionGroup.Functions.Length)

[<Fact>]
let ``By cursor, get call syntax token 4`` () =
    let src =
        """
#[intrinsic("int32")]
alias int32

class C =

    GetSomething(x: int32): () = ()

main(): () =
    let c = C()
    c.GetSomething(~^~)
        """
    let info = getFunctionCallInfoByCursor src
    let symbol = info.Function
    Assert.False(symbol.IsFunctionGroup)
    Assert.True(symbol.IsFunction)
    Assert.Equal(0, info.ActiveParameterIndex)
    Assert.Equal(0, info.ActiveFunctionIndex)

[<Fact>]
let ``By cursor, get call syntax token 5`` () =
    let src =
        """
#[intrinsic("int32")]
alias int32

class C =

    GetSomething<T>(x: int32): () = ()
    GetSomething<T, U>(x: int32, y: int32): () = ()

main(): () =
    let c = C()
    c.GetSomething(1,~^~)
        """
    let info = getFunctionCallInfoByCursor src
    let symbol = info.Function
    Assert.False(symbol.IsFunctionGroup)
    Assert.True(symbol.IsFunction)
    Assert.Equal(1, info.ActiveParameterIndex)
    Assert.Equal(0, info.ActiveFunctionIndex)

[<Fact>]
let ``By cursor, get call syntax token 6`` () =
    let src =
        """
#[intrinsic("int32")]
alias int32

class C =

    GetSomething<T>(x: int32): () = ()
    GetSomething<T, U>(x: int32, y: int32): () = ()
    GetSomething<T, U, V>(x: int32, y: int32, z: int32): () = ()

main(): () =
    let c = C()
    c.GetSomething(1, 2,~^~)
        """
    let info = getFunctionCallInfoByCursor src
    let symbol = info.Function
    Assert.False(symbol.IsFunctionGroup)
    Assert.True(symbol.IsFunction)
    Assert.Equal(2, info.ActiveParameterIndex)
    Assert.Equal(0, info.ActiveFunctionIndex)
