﻿module ReferenceTests

open Xunit
open TestUtilities
open Utilities

[<Fact>]
let ``Basic reference should compile and run``() =
    let refSrc =
        """
module ReferenceTest

#[intrinsic("print")]
print(__oly_object): ()

RefTest(): () = print("from a reference")
        """
    let src =
        """
open static ReferenceTest

main(): () =
    RefTest()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "from a reference"

[<Fact>]
let ``Witness pass subsumption``() =
    let refSrc =
        """
module ReferenceTest

#[intrinsic("int32")]
alias int32

#[intrinsic("print")]
print(__oly_object): ()

interface Add<T1, T2, T3>

test<T>(): () where T: Add<T, T, T> = print("from a reference")
        """
    let src =
        """
open extension Int32AddExtension
open static ReferenceTest

interface Add<T> =
    inherits Add<T, T, T>

extension Int32AddExtension =
    inherits int32
    implements Add<int32>

main(): () =
    test<int32>()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "from a reference"

[<Fact>]
let ``Witness pass subsumption 2``() =
    let refSrc =
        """
module ReferenceTest

#[intrinsic("int32")]
alias int32

#[intrinsic("print")]
print(__oly_object): ()

interface Add<T1, T2, T3>
        """
    let src =
        """
open extension Int32AddExtension
open static ReferenceTest

interface Add<T> =
    inherits Add<T, T, T>

extension Int32AddExtension =
    inherits int32
    implements Add<int32>

test<T>(): () where T: Add<T, T, T> = print("not from a reference")

main(): () =
    test<int32>()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "not from a reference"

[<Fact>]
let ``Witness pass subsumption 3``() =
    let refSrc =
        """
module ReferenceTest

#[intrinsic("int32")]
alias int32

#[intrinsic("print")]
print(__oly_object): ()

interface Add<T1, T2, T3> =

    static abstract test(): ()

test<T>(): () where T: Add<T, T, T> = 
    T.test()
    print("from a reference")

        """
    let src =
        """
open extension Int32AddExtension
open static ReferenceTest

interface Add<T> =
    inherits Add<T, T, T>

extension Int32AddExtension =
    inherits int32
    implements Add<int32>

    static overrides test(): () = ()

main(): () =
    test<int32>()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "from a reference"

[<Fact>]
let ``Witness pass subsumption 4``() =
    let refSrc =
        """
module ReferenceTest

#[intrinsic("int32")]
alias int32

#[intrinsic("print")]
print(__oly_object): ()

interface Add<T1, T2, T3> =

    static abstract test(): ()

        """
    let src =
        """
open extension Int32AddExtension
open static ReferenceTest

interface Add<T> =
    inherits Add<T, T, T>

extension Int32AddExtension =
    inherits int32
    implements Add<int32>

    static overrides test(): () = ()

test<T>(): () where T: Add<T, T, T> = 
    T.test()
    print("not from a reference")

main(): () =
    test<int32>()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "not from a reference"

[<Fact>]
let ``Basic namespace reference should compile and run``() =
    let refSrc =
        """
namespace Test

module TestModule =

    #[intrinsic("print")]
    print(__oly_object): ()

    RefTest(): () = print("from a reference")
        """
    let src =
        """
namespace Test

module TestMain =

    main(): () =
        TestModule.RefTest()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "from a reference"

[<Fact>]
let ``Basic namespace reference should compile and run 2``() =
    let refSrc =
        """
namespace Test.Inner

module TestModule =

    #[intrinsic("print")]
    print(__oly_object): ()

    RefTest(): () = print("from a reference")
        """
    let src =
        """
namespace Test.Inner

module TestMain =

    main(): () =
        TestModule.RefTest()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "from a reference"

[<Fact>]
let ``Basic module reference should compile and run``() =
    let refSrc =
        """
module TestModule

#[intrinsic("print")]
print(__oly_object): ()

RefTest(): () = print("from a reference")
        """
    let src =
        """
namespace Test.Inner

open static TestModule

module TestMain =

    main(): () =
        RefTest()
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "from a reference"

[<Fact>]
let ``Basic types referencing one another in the same compilation``() =
    let src1 =
        """
namespace Test

module TestModule =

    #[intrinsic("print")]
    print(__oly_object): ()

    test(): () =
        let a = A()
        a.Call()

        """
    let src2 =
        """
namespace Test

open static Test.TestModule

class A =

    Call(): () = print("call")

module TestMain =

    main(): () =
        print("hello")
        test()
        """
    (OlyTwo src2 src1).Compilation
    |> runWithExpectedOutput "hellocall"

[<Fact>]
let ``Basic array types referencing one another in the different compilations``() =
    let refSrc =
        """
namespace Test

module TestModule =

    struct Item =
        public mutable field X: int32 = 0

    #[intrinsic("int32")]
    alias int32

    #[intrinsic("get_element")]
    (`[]`)<T>(T[], index: int32): T
    #[intrinsic("get_element")]
    (`[,]`)<T>(T[,], index1: int32, index2: int32): T

    #[intrinsic("get_element")]
    (`[]`)<T>(mutable T[], index: int32): T
    #[intrinsic("set_element")]
    (`[]`)<T>(mutable T[], index: int32, T): ()
    #[intrinsic("get_element")]
    (`[,]`)<T>(mutable T[,], index1: int32, index2: int32): T
    #[intrinsic("set_element")]
    (`[,]`)<T>(mutable T[,], index1: int32, index2: int32, T): ()

    #[intrinsic("print")]
    print(__oly_object): ()

    test(): (Item[])[] =
        [[Item()]]

        """
    let src =
        """
open static Test.TestModule

main(): () =
    let values = test()
    let mutable values = values
    let value = (values[0])[0]
    print(value.X)
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "0"

[<Fact>]
let ``() -> () function should be properly exported and imported``() =
    let refSrc =
        """
namespace Test

module TestModule =

    #[intrinsic("print")]
    print(__oly_object): ()

    test(f: () -> ()): () =
        f()

        """
    let src =
        """
open static Test.TestModule

main(): () =
    test(() -> print("123"))
        """
    OlyWithRef refSrc src
    |> withCompile
    |> shouldRunWithExpectedOutput "123"
