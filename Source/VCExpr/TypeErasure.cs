//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Text;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Contracts;
using Microsoft.Basetypes;
using Microsoft.Boogie.VCExprAST;

// different classes for erasing complex types in VCExprs, replacing them
// with axioms that can be handled by theorem provers and SMT solvers

namespace Microsoft.Boogie.TypeErasure
{
  using Microsoft.Boogie.VCExprAST;

  // some functionality that is needed in many places (and that should
  // really be provided by the Spec# container classes; maybe one
  // could integrate the functions in a nicer way?)
  public class HelperFuns {
    
    public static Function! BoogieFunction(string! name, List<TypeVariable!>! typeParams,
                                           params Type[]! types)
      requires types.Length > 0;
      requires forall{int i in (0:types.Length); types[i] != null};
    {
      VariableSeq! args = new VariableSeq ();
      for (int i = 0; i < types.Length - 1; ++i)
        args.Add(new Formal (Token.NoToken,
                             new TypedIdent (Token.NoToken, "arg" + i, (!)types[i]),
                             true));
      Formal! result = new Formal (Token.NoToken,
                                   new TypedIdent (Token.NoToken, "res",
                                                   (!)types[types.Length - 1]),
                                   false);
      return new Function (Token.NoToken, name, ToSeq(typeParams), args, result);
    }

    public static Function! BoogieFunction(string! name, params Type[]! types) {
      return BoogieFunction(name, new List<TypeVariable!> (), types);
    }

    // boogie function where all arguments and the result have the same type U
    public static Function! UniformBoogieFunction(string! name, int arity, Type! U) {
      Type[]! types = new Type [arity + 1];
      for (int i = 0; i < arity + 1; ++i)
        types[i] = U;
      return BoogieFunction(name, types);
    }

    public static List<VCExprVar!>! GenVarsForInParams(Function! fun,
                                                       VCExpressionGenerator! gen) {
      List<VCExprVar!>! arguments = new List<VCExprVar!> (fun.InParams.Length);
      foreach (Formal! f in fun.InParams) {
        VCExprVar! var = gen.Variable(f.Name, f.TypedIdent.Type);
        arguments.Add(var);
      }
      return arguments;
    }

    public static List<T!>! ToList<T> (params T[]! args) {
      List<T!>! res = new List<T!> (args.Length);
      foreach (T t in args)
        res.Add((!)t);
      return res;
    }

    public static List<TypeVariable!>! ToList(TypeVariableSeq! seq) {
      List<TypeVariable!>! res = new List<TypeVariable!> (seq.Length);
      foreach (TypeVariable! var in seq)
        res.Add(var);
      return res;
    }

    public static TypeVariableSeq! ToSeq(List<TypeVariable!>! list) {
      TypeVariableSeq! res = new TypeVariableSeq ();
      foreach (TypeVariable! var in list)
        res.Add(var);
      return res;
    }

    public static List<T>! Intersect<T>(List<T>! a, List<T>! b) {
      List<T>! res = new List<T> (Math.Min(a.Count, b.Count));
      foreach (T x in a)
        if (b.Contains(x))
          res.Add(x);
      res.TrimExcess();
      return res;
    }

    public static List<KeyValuePair<T1, T2>>! ToPairList<T1, T2>(IDictionary<T1, T2>! dict) {
      List<KeyValuePair<T1, T2>>! res = new List<KeyValuePair<T1, T2>> (dict);
      return res;
    }

    public static void AddRangeWithoutDups<T>(IEnumerable<T>! fromList, List<T>! toList) {
      foreach (T t in fromList)
        if (!toList.Contains(t))
          toList.Add(t);
    }

    public static void AddFreeVariablesWithoutDups(Type! type, List<TypeVariable!>! toList) {
      foreach (TypeVariable! var in type.FreeVariables) {
        if (!toList.Contains(var))
          toList.Add(var);
      }
    }

    public static List<VCExpr!>! ToVCExprList(List<VCExprVar!>! list) {
      List<VCExpr!>! res = new List<VCExpr!> (list.Count);
      foreach (VCExprVar! var in list)
        res.Add(var);
      return res;
    }

    public static List<VCExprVar!>! VarVector(string! baseName, int num, Type! type,
                                              VCExpressionGenerator! gen) {
      List<VCExprVar!>! res = new List<VCExprVar!> (num);
      for (int i = 0; i < num; ++i)
        res.Add(gen.Variable(baseName + i, type));
      return res;
    }
    
    public static List<VCExprVar!>! VarVector(string! baseName, List<Type!>! types,
                                              VCExpressionGenerator! gen) {
      List<VCExprVar!>! res = new List<VCExprVar!> (types.Count);
      for (int i = 0; i < types.Count; ++i)
        res.Add(gen.Variable(baseName + i, types[i]));
      return res;
    }
  }

  //////////////////////////////////////////////////////////////////////////////

  internal struct TypeCtorRepr {
    // function that represents the application of the type constructor
    // to smaller types
    public readonly Function! Ctor;
    // left-inverse functions that extract the subtypes of a compound type
    public readonly List<Function!>! Dtors;

    public TypeCtorRepr(Function! ctor, List<Function!>! dtors)
      requires ctor.InParams.Length == dtors.Count; {
      this.Ctor = ctor;
      this.Dtors = dtors;
    }
  }

  //////////////////////////////////////////////////////////////////////////////

  // The class responsible for creating and keeping track of all
  // axioms related to the type system. This abstract class is made
  // concrete in two subclasses, one for type erasure with type
  // premisses in quantifiers (the semantic approach), and one for
  // type erasure with explicit type arguments of polymorphic
  // functions (the syntacted approach).
  public abstract class TypeAxiomBuilder : ICloneable {

    protected readonly VCExpressionGenerator! Gen;

    internal abstract MapTypeAbstractionBuilder! MapTypeAbstracter { get; }

    ///////////////////////////////////////////////////////////////////////////
    // Type Axioms

    // list in which all typed axioms are collected
    private readonly List<VCExpr!>! AllTypeAxioms;

    // list in which type axioms are incrementally collected
    private readonly List<VCExpr!>! IncTypeAxioms;

    internal void AddTypeAxiom(VCExpr! axiom) {
      AllTypeAxioms.Add(axiom);
      IncTypeAxioms.Add(axiom);
    }

    // Return all axioms that were added since the last time NewAxioms
    // was called
    public VCExpr! GetNewAxioms() {
      VCExpr! res = Gen.NAry(VCExpressionGenerator.AndOp, IncTypeAxioms);
      IncTypeAxioms.Clear();
      return res;
    }

    // mapping from a type to its constructor number/index
    private readonly Function! Ctor;
    private BigNum CurrentCtorNum;

    private VCExpr! GenCtorAssignment(VCExpr! typeRepr) {
      if (CommandLineOptions.Clo.TypeEncodingMethod
                == CommandLineOptions.TypeEncoding.None)
        return VCExpressionGenerator.True;

      VCExpr! res = Gen.Eq(Gen.Function(Ctor, typeRepr),
                           Gen.Integer(CurrentCtorNum));
      CurrentCtorNum = CurrentCtorNum + BigNum.ONE;
      return res;
    }

    private VCExpr! GenCtorAssignment(Function! typeRepr) {
      if (CommandLineOptions.Clo.TypeEncodingMethod
                == CommandLineOptions.TypeEncoding.None)
        return VCExpressionGenerator.True;

      List<VCExprVar!>! quantifiedVars = HelperFuns.GenVarsForInParams(typeRepr, Gen);
      VCExpr! eq =
        GenCtorAssignment(Gen.Function(typeRepr,
                                       HelperFuns.ToVCExprList(quantifiedVars)));

      if (typeRepr.InParams.Length == 0)
        return eq;

      return Gen.Forall(quantifiedVars, new List<VCTrigger!> (),
                        "ctor:" + typeRepr.Name, eq);
    }

    // generate an axiom (forall x0, x1, ... :: invFun(fun(x0, x1, ...) == xi)
    protected VCExpr! GenLeftInverseAxiom(Function! fun, Function! invFun, int dtorNum) {
      List<VCExprVar!>! quantifiedVars = HelperFuns.GenVarsForInParams(fun, Gen);

      VCExpr! funApp = Gen.Function(fun, HelperFuns.ToVCExprList(quantifiedVars));
      VCExpr! lhs = Gen.Function(invFun, funApp);
      VCExpr! rhs = quantifiedVars[dtorNum];
      VCExpr! eq = Gen.Eq(lhs, rhs);

      List<VCTrigger!>! triggers = HelperFuns.ToList(Gen.Trigger(true, HelperFuns.ToList(funApp)));
      return Gen.Forall(quantifiedVars, triggers, "typeInv:" + invFun.Name, eq);
    }

    ///////////////////////////////////////////////////////////////////////////

    // the type of everything that is not int, bool, or a type
    private readonly TypeCtorDecl! UDecl;
    public readonly Type! U;

    // the type of types
    private readonly TypeCtorDecl! TDecl;
    public readonly Type! T;

    public abstract Type! TypeAfterErasure(Type! type);
    public abstract bool UnchangedType(Type! type);

    ///////////////////////////////////////////////////////////////////////////
    // Symbols for representing types

    private readonly IDictionary<Type!, VCExpr!>! BasicTypeReprs;

    private VCExpr! GetBasicTypeRepr(Type! type)
      requires type.IsBasic || type.IsBv; {
      VCExpr res;
      if (!BasicTypeReprs.TryGetValue(type, out res)) {
        res = Gen.Function(HelperFuns.BoogieFunction(type.ToString() + "Type", T));
        AddTypeAxiom(GenCtorAssignment(res));
        BasicTypeReprs.Add(type, res);
      }
      return (!)res;
    }

    private readonly IDictionary<TypeCtorDecl!, TypeCtorRepr>! TypeCtorReprs;

    internal TypeCtorRepr GetTypeCtorReprStruct(TypeCtorDecl! decl) {
      TypeCtorRepr reprSet;
      if (!TypeCtorReprs.TryGetValue(decl, out reprSet)) {
        Function! ctor = HelperFuns.UniformBoogieFunction(decl.Name + "Type", decl.Arity, T);
        AddTypeAxiom(GenCtorAssignment(ctor));

        List<Function!>! dtors = new List<Function!>(decl.Arity);
        for (int i = 0; i < decl.Arity; ++i) {
          Function! dtor = HelperFuns.UniformBoogieFunction(decl.Name + "TypeInv" + i, 1, T);
          dtors.Add(dtor);
          AddTypeAxiom(GenLeftInverseAxiom(ctor, dtor, i));
        }

        reprSet = new TypeCtorRepr(ctor, dtors);
        TypeCtorReprs.Add(decl, reprSet);
      }

      return reprSet;
    }

    public Function! GetTypeCtorRepr(TypeCtorDecl! decl) {
      return GetTypeCtorReprStruct(decl).Ctor;
    }

    public Function! GetTypeDtor(TypeCtorDecl! decl, int num) {
      return GetTypeCtorReprStruct(decl).Dtors[num];
    }

    // mapping from free type variables to VCExpr variables
    private readonly IDictionary<TypeVariable!, VCExprVar!>! TypeVariableMapping;

    public VCExprVar! Typed2Untyped(TypeVariable! var) {
      VCExprVar res;
      if (!TypeVariableMapping.TryGetValue(var, out res)) {
        res = new VCExprVar (var.Name, T);
        TypeVariableMapping.Add(var, res);
      }
      return (!)res;
    }


    ////////////////////////////////////////////////////////////////////////////
    // Symbols for representing variables and constants

    // Globally defined variables
    private readonly IDictionary<VCExprVar!, VCExprVar!>! Typed2UntypedVariables;

    // This method must only be used for free (unbound) variables
    public VCExprVar! Typed2Untyped(VCExprVar! var) {
      VCExprVar res;
      if (!Typed2UntypedVariables.TryGetValue(var, out res)) {
        res = Gen.Variable(var.Name, TypeAfterErasure(var.Type));
        Typed2UntypedVariables.Add(var, res);
        AddVarTypeAxiom(res, var.Type);
      }
      return (!)res;
    }

    protected abstract void AddVarTypeAxiom(VCExprVar! var, Type! originalType);

    ///////////////////////////////////////////////////////////////////////////
    // Translation function from types to their term representation

    public VCExpr! Type2Term(Type! type,
                             IDictionary<TypeVariable!, VCExpr!>! varMapping) {
        //
      if (type.IsBasic || type.IsBv) {
        //
        return GetBasicTypeRepr(type);
        //
      } else if (type.IsCtor) {
        //
        CtorType ctype = type.AsCtor;
        Function! repr = GetTypeCtorRepr(ctype.Decl);
        List<VCExpr!>! args = new List<VCExpr!> (ctype.Arguments.Length);
        foreach (Type! t in ctype.Arguments)
          args.Add(Type2Term(t, varMapping));
        return Gen.Function(repr, args);
        //
      } else if (type.IsVariable) {
        //
        VCExpr res;
        if (!varMapping.TryGetValue(type.AsVariable, out res))
          // then the variable is free and we bind it at this point to a term
          // variable
          res = Typed2Untyped(type.AsVariable);
        return (!)res;
        //
      } else if (type.IsMap) {
        //
        return Type2Term(MapTypeAbstracter.AbstractMapType(type.AsMap), varMapping);
        //
      } else {
        System.Diagnostics.Debug.Fail("Don't know how to handle this type: " + type);
        assert false;  // please the compiler
      }
    }

    ////////////////////////////////////////////////////////////////////////////

    public TypeAxiomBuilder(VCExpressionGenerator! gen) {
      this.Gen = gen;
      AllTypeAxioms = new List<VCExpr!> ();
      IncTypeAxioms = new List<VCExpr!> ();
      BasicTypeReprs = new Dictionary<Type!, VCExpr!> ();
      CurrentCtorNum = BigNum.ZERO;
      TypeCtorReprs = new Dictionary<TypeCtorDecl!, TypeCtorRepr> ();
      TypeVariableMapping = new Dictionary<TypeVariable!, VCExprVar!> ();
      Typed2UntypedVariables = new Dictionary<VCExprVar!, VCExprVar!> ();

      TypeCtorDecl! uDecl = new TypeCtorDecl(Token.NoToken, "U", 0);
      UDecl = uDecl;
      Type! u = new CtorType (Token.NoToken, uDecl, new TypeSeq ());
      U = u;

      TypeCtorDecl! tDecl = new TypeCtorDecl(Token.NoToken, "T", 0);
      TDecl = tDecl;
      Type! t = new CtorType (Token.NoToken, tDecl, new TypeSeq ());
      T = t;

      Ctor = HelperFuns.BoogieFunction("Ctor", t, Type.Int);
    }

    public virtual void Setup() {
      GetBasicTypeRepr(Type.Int);
      GetBasicTypeRepr(Type.Bool);
    }

    // constructor to allow cloning
    internal TypeAxiomBuilder(TypeAxiomBuilder! builder) {
      Gen = builder.Gen;
      AllTypeAxioms = new List<VCExpr!> (builder.AllTypeAxioms);
      IncTypeAxioms = new List<VCExpr!> (builder.IncTypeAxioms);

      UDecl = builder.UDecl;
      U = builder.U;

      TDecl = builder.TDecl;
      T = builder.T;

      Ctor = builder.Ctor;
      CurrentCtorNum = builder.CurrentCtorNum;

      BasicTypeReprs = new Dictionary<Type!, VCExpr!> (builder.BasicTypeReprs);
      TypeCtorReprs = new Dictionary<TypeCtorDecl!, TypeCtorRepr> (builder.TypeCtorReprs);

      TypeVariableMapping =
        new Dictionary<TypeVariable!, VCExprVar!> (builder.TypeVariableMapping);
      Typed2UntypedVariables =
        new Dictionary<VCExprVar!, VCExprVar!> (builder.Typed2UntypedVariables);
    }

    public abstract Object! Clone();
  }

  //////////////////////////////////////////////////////////////////////////////

  // Subclass of the TypeAxiomBuilder that provides all functionality
  // to deal with native sorts of a theorem prover (that are the only
  // types left after erasing all other types). Currently, these are:
  //
  //  U ... sort of all individuals/objects/values
  //  T ... sort of all types
  //  int ... integers
  //  bool ... booleans

  public abstract class TypeAxiomBuilderIntBoolU : TypeAxiomBuilder {

    public TypeAxiomBuilderIntBoolU(VCExpressionGenerator! gen) {
      base(gen);
      TypeCasts = new Dictionary<Type!, TypeCastSet> ();
    }

    // constructor to allow cloning
    internal TypeAxiomBuilderIntBoolU(TypeAxiomBuilderIntBoolU! builder) {
      base(builder);
      TypeCasts = new Dictionary<Type!, TypeCastSet> (builder.TypeCasts);
    }

    public override void Setup() {
      base.Setup();

      GetTypeCasts(Type.Int);
      GetTypeCasts(Type.Bool);
    }

    // generate inverse axioms for casts (castToU(castFromU(x)) = x, under certain premisses)
    protected abstract VCExpr! GenReverseCastAxiom(Function! castToU, Function! castFromU);

    protected VCExpr! GenReverseCastEq(Function! castToU, Function! castFromU,
                                       out VCExprVar! var, out List<VCTrigger!>! triggers) {
      var = Gen.Variable("x", U);

      VCExpr inner = Gen.Function(castFromU, var);
      VCExpr lhs = Gen.Function(castToU, inner);
      triggers = HelperFuns.ToList(Gen.Trigger(true, HelperFuns.ToList(inner)));

      return Gen.Eq(lhs, var);
    }

    protected abstract VCExpr! GenCastTypeAxioms(Function! castToU, Function! castFromU);

    ///////////////////////////////////////////////////////////////////////////
    // storage of type casts for types that are supposed to be left over in the
    // VCs (like int, bool, bitvectors)

    private readonly IDictionary<Type!, TypeCastSet>! TypeCasts;

    private TypeCastSet GetTypeCasts(Type! type) {
      TypeCastSet res;
      if (!TypeCasts.TryGetValue(type, out res)) {
        Function! castToU = HelperFuns.BoogieFunction(type.ToString() + "_2_U", type, U);
        Function! castFromU = HelperFuns.BoogieFunction("U_2_" + type.ToString(), U, type);

        AddTypeAxiom(GenLeftInverseAxiom(castToU, castFromU, 0));
        AddTypeAxiom(GenReverseCastAxiom(castToU, castFromU));
        AddTypeAxiom(GenCastTypeAxioms(castToU, castFromU));

        res = new TypeCastSet (castToU, castFromU);
        TypeCasts.Add(type, res);
      }
      return res;
    }

    public Function! CastTo(Type! type)
      requires UnchangedType(type); {
      return GetTypeCasts(type).CastFromU;
    }

    public Function! CastFrom(Type! type)
      requires UnchangedType(type); {
      return GetTypeCasts(type).CastToU;
    }

    private struct TypeCastSet {
      public readonly Function! CastToU;
      public readonly Function! CastFromU;

      public TypeCastSet(Function! castToU, Function! castFromU) {
        CastToU = castToU;
        CastFromU = castFromU;
      }
    }

    public bool IsCast(Function! fun) {
      if (fun.InParams.Length != 1)
        return false;
      Type! inType = ((!)fun.InParams[0]).TypedIdent.Type;
      if (inType.Equals(U)) {
        Type! outType = ((!)fun.OutParams[0]).TypedIdent.Type;
        if (!TypeCasts.ContainsKey(outType))
          return false;
        return fun.Equals(CastTo(outType));
      } else {
        if (!TypeCasts.ContainsKey(inType))
          return false;
        Type! outType = ((!)fun.OutParams[0]).TypedIdent.Type;
        if (!outType.Equals(U))
          return false;
        return fun.Equals(CastFrom(inType));
      }
    }

    ////////////////////////////////////////////////////////////////////////////

    // the only types that we allow in "untyped" expressions are U,
    // Type.Int, and Type.Bool

    public override Type! TypeAfterErasure(Type! type) {
      if (UnchangedType(type))
        // these types are kept
        return type;
      else
        // all other types are replaced by U
        return U;
    }

    [Pure]
    public override bool UnchangedType(Type! type) {
      return type.IsInt || type.IsBool || type.IsBv || (type.IsMap && CommandLineOptions.Clo.UseArrayTheory);
    }    

    public VCExpr! Cast(VCExpr! expr, Type! toType)
      requires expr.Type.Equals(U) || UnchangedType(expr.Type);
      requires toType.Equals(U) || UnchangedType(toType);
    {
      if (expr.Type.Equals(toType))
        return expr;

      if (toType.Equals(U)) {
        return Gen.Function(CastFrom(expr.Type), expr);
      } else {
        assert expr.Type.Equals(U);
        return Gen.Function(CastTo(toType), expr);
      }
    }

    public List<VCExpr!>! CastSeq(List<VCExpr!>! exprs, Type! toType) {
      List<VCExpr!>! res = new List<VCExpr!> (exprs.Count);
      foreach (VCExpr! expr in exprs)
        res.Add(Cast(expr, toType));
      return res;
    }


  }

  //////////////////////////////////////////////////////////////////////////////
  // Class for computing most general abstractions of map types. An abstraction
  // of a map type t is a maptype t' in which closed proper subtypes have been replaced
  // with type variables. E.g., an abstraction of <a>[C a, int]a would be <a>[C a, b]a.
  // We subsequently consider most general abstractions as ordinary parametrised types,
  // i.e., "<a>[C a, b]a" would be considered as a type "M b" with polymorphically typed
  // access functions
  //
  //            select<a,b>(M b, C a, b) returns (a)
  //            store<a,b>(M b, C a, b, a) returns (M b)

  internal abstract class MapTypeAbstractionBuilder {

    protected readonly TypeAxiomBuilder! AxBuilder;
    protected readonly VCExpressionGenerator! Gen;

    internal MapTypeAbstractionBuilder(TypeAxiomBuilder! axBuilder,
                                       VCExpressionGenerator! gen) {
      this.AxBuilder = axBuilder;
      this.Gen = gen;
      AbstractionVariables = new List<TypeVariable!> ();
      ClassRepresentations = new Dictionary<MapType!, MapTypeClassRepresentation> ();
    }

    // constructor for cloning
    internal MapTypeAbstractionBuilder(TypeAxiomBuilder! axBuilder,
                                       VCExpressionGenerator! gen,
                                       MapTypeAbstractionBuilder! builder) {
      this.AxBuilder = axBuilder;
      this.Gen = gen;
      AbstractionVariables =
        new List<TypeVariable!> (builder.AbstractionVariables);
      ClassRepresentations =
        new Dictionary<MapType!, MapTypeClassRepresentation> (builder.ClassRepresentations);
    }

    ///////////////////////////////////////////////////////////////////////////
    // Type variables used in the abstractions. We use the same variables in the
    // same order in all abstractions in order to obtain comparable abstractions
    // (equals, hashcode)

    private readonly List<TypeVariable!>! AbstractionVariables;

    private TypeVariable! AbstractionVariable(int num)
      requires num >= 0; {
      while (AbstractionVariables.Count <= num)
        AbstractionVariables.Add(new TypeVariable (Token.NoToken,
                                                   "aVar" + AbstractionVariables.Count));
      return AbstractionVariables[num];
    }

    ///////////////////////////////////////////////////////////////////////////
    // The untyped representation of a class of map types, i.e., of a map type
    // <a0, a1, ...>[A0, A1, ...] R, where the argument types and the result type
    // possibly contain free type variables. For each such class, a separate type
    // constructor and separate select/store functions are introduced.

    protected struct MapTypeClassRepresentation {
      public readonly TypeCtorDecl! RepresentingType;
      public readonly Function! Select;
      public readonly Function! Store;

      public MapTypeClassRepresentation(TypeCtorDecl! representingType,
                                        Function! select, Function! store) {
        this.RepresentingType = representingType;
        this.Select = select;
        this.Store = store;
      }
    }

    private readonly IDictionary<MapType!, MapTypeClassRepresentation>! ClassRepresentations;

    protected MapTypeClassRepresentation GetClassRepresentation(MapType! abstractedType) {
      MapTypeClassRepresentation res;
      if (!ClassRepresentations.TryGetValue(abstractedType, out res)) {
        int num = ClassRepresentations.Count;
        TypeCtorDecl! synonym =
          new TypeCtorDecl(Token.NoToken, "MapType" + num, abstractedType.FreeVariables.Length);

        Function! select, store;
        GenSelectStoreFunctions(abstractedType, synonym, out select, out store);

        res = new MapTypeClassRepresentation(synonym, select, store);
        ClassRepresentations.Add(abstractedType, res);
      }
      return res;
    }

    // the actual select and store functions are generated by the
    // concrete subclasses of this class
    protected abstract void GenSelectStoreFunctions(MapType! abstractedType,
                                                    TypeCtorDecl! synonymDecl,
                                                    out Function! select, out Function! store);

    ///////////////////////////////////////////////////////////////////////////

    public Function! Select(MapType! rawType, out TypeSeq! instantiations) {
      return AbstractAndGetRepresentation(rawType, out instantiations).Select;
    }

    public Function! Store(MapType! rawType, out TypeSeq! instantiations) {
      return AbstractAndGetRepresentation(rawType, out instantiations).Store;
    }

    private MapTypeClassRepresentation
            AbstractAndGetRepresentation(MapType! rawType, out TypeSeq! instantiations) {
      instantiations = new TypeSeq ();
      MapType! abstraction = ThinOutMapType(rawType, instantiations);
      return GetClassRepresentation(abstraction);
    }

    public CtorType! AbstractMapType(MapType! rawType) {
      TypeSeq! instantiations = new TypeSeq ();
      MapType! abstraction = ThinOutMapType(rawType, instantiations);

      MapTypeClassRepresentation repr = GetClassRepresentation(abstraction);
      assume repr.RepresentingType.Arity == instantiations.Length;
      return new CtorType(Token.NoToken, repr.RepresentingType, instantiations);
    }

    // TODO: cache the result of this operation
    protected MapType! ThinOutMapType(MapType! rawType,
                                      TypeSeq! instantiations) {
      TypeSeq! newArguments = new TypeSeq ();
      foreach (Type! subtype in rawType.Arguments)
        newArguments.Add(ThinOutType(subtype, rawType.TypeParameters,
                                     instantiations));
      Type! newResult = ThinOutType(rawType.Result, rawType.TypeParameters,
                                    instantiations);
      return new MapType(Token.NoToken, rawType.TypeParameters, newArguments, newResult);
    }

    private Type! ThinOutType(Type! rawType, TypeVariableSeq! boundTypeParams,
                              // the instantiations of inserted type variables,
                              // the order corresponds to the order in which
                              // "AbstractionVariable(int)" delivers variables
                              TypeSeq! instantiations) {

      if (CommandLineOptions.Clo.Monomorphize && AxBuilder.UnchangedType(rawType))
        return rawType;

      if (forall{TypeVariable! var in rawType.FreeVariables;
          !boundTypeParams.Has(var)}) {
        // Bingo!
        // if the type does not contain any bound variables, we can simply
        // replace it with a type variable
        TypeVariable! abstractionVar = AbstractionVariable(instantiations.Length);
        assume !boundTypeParams.Has(abstractionVar);
        instantiations.Add(rawType);
        return abstractionVar;
      }

      if (rawType.IsVariable) {
        //
        // then the variable has to be bound, we cannot do anything
        TypeVariable! rawVar = rawType.AsVariable;
        assume boundTypeParams.Has(rawVar);
        return rawVar;
        //
      } else if (rawType.IsMap) {
        //
        // recursively abstract this map type and continue abstracting
        CtorType! abstraction = AbstractMapType(rawType.AsMap);
        return ThinOutType(abstraction, boundTypeParams, instantiations);
        //
      } else if (rawType.IsCtor) {
        //
        // traverse the subtypes
        CtorType! rawCtorType = rawType.AsCtor;
        TypeSeq! newArguments = new TypeSeq ();
        foreach (Type! subtype in rawCtorType.Arguments)
          newArguments.Add(ThinOutType(subtype, boundTypeParams,
                                       instantiations));
        return new CtorType(Token.NoToken, rawCtorType.Decl, newArguments);
        //
      } else {
        System.Diagnostics.Debug.Fail("Don't know how to handle this type: " + rawType);
        return rawType;   // compiler appeasement policy
      }
    }

  }

  //////////////////////////////////////////////////////////////////////////////

  public class VariableBindings {
    public readonly IDictionary<VCExprVar!, VCExprVar!>! VCExprVarBindings;
    public readonly IDictionary<TypeVariable!, VCExpr!>! TypeVariableBindings;
    
    public VariableBindings(IDictionary<VCExprVar!, VCExprVar!>! vcExprVarBindings,
                            IDictionary<TypeVariable!, VCExpr!>! typeVariableBindings) {
      this.VCExprVarBindings = vcExprVarBindings;
      this.TypeVariableBindings = typeVariableBindings;
    }

    public VariableBindings() {
      this (new Dictionary<VCExprVar!, VCExprVar!> (),
            new Dictionary<TypeVariable!, VCExpr!> ());
    }

    public VariableBindings! Clone() {
      IDictionary<VCExprVar!, VCExprVar!>! newVCExprVarBindings =
        new Dictionary<VCExprVar!, VCExprVar!> ();
      foreach (KeyValuePair<VCExprVar!, VCExprVar!> pair in VCExprVarBindings)
        newVCExprVarBindings.Add(pair);
      IDictionary<TypeVariable!, VCExpr!>! newTypeVariableBindings =
        new Dictionary<TypeVariable!, VCExpr!> ();
      foreach (KeyValuePair<TypeVariable!, VCExpr!> pair in TypeVariableBindings)
        newTypeVariableBindings.Add(pair);
      return new VariableBindings(newVCExprVarBindings, newTypeVariableBindings);
    }
  }
    
  //////////////////////////////////////////////////////////////////////////////

  // The central class for turning types VCExprs into untyped
  // VCExprs. This class makes use of the type axiom builder to manage
  // the available types and symbols.

  public abstract class TypeEraser : MutatingVCExprVisitor<VariableBindings!> {

    protected readonly TypeAxiomBuilderIntBoolU! AxBuilder;

    protected abstract OpTypeEraser! OpEraser { get; }
    
    ////////////////////////////////////////////////////////////////////////////

    public TypeEraser(TypeAxiomBuilderIntBoolU! axBuilder, VCExpressionGenerator! gen) {
      base(gen);
      AxBuilder = axBuilder;
    }

    public VCExpr! Erase(VCExpr! expr, int polarity)
      requires polarity >= -1 && polarity <= 1; {
      this.Polarity = polarity;
      return Mutate(expr, new VariableBindings());
    }

    internal int Polarity = 1;  // 1 for positive, -1 for negative, 0 for both

    ////////////////////////////////////////////////////////////////////////////

    public override VCExpr! Visit(VCExprLiteral! node, VariableBindings! bindings) {
      assume node.Type == Type.Bool || node.Type == Type.Int;
      return node;
    }

    ////////////////////////////////////////////////////////////////////////////

    public override VCExpr! Visit(VCExprNAry! node, VariableBindings! bindings) {
      VCExprOp! op = node.Op;
      if (op == VCExpressionGenerator.AndOp || op == VCExpressionGenerator.OrOp)
        // more efficient on large conjunctions/disjunctions
        return base.Visit(node, bindings);

      // the visitor that handles all other operators
      return node.Accept<VCExpr!, VariableBindings!>(OpEraser, bindings);
    }

    // this method is called by MutatingVCExprVisitor.Visit(VCExprNAry, ...)
    protected override VCExpr! UpdateModifiedNode(VCExprNAry! originalNode,
                                                  List<VCExpr!>! newSubExprs,
                                                  bool changed,
                                                  VariableBindings! bindings) {
      assume originalNode.Op == VCExpressionGenerator.AndOp ||
             originalNode.Op == VCExpressionGenerator.OrOp;
      return Gen.Function(originalNode.Op,
                          AxBuilder.Cast(newSubExprs[0], Type.Bool),
                          AxBuilder.Cast(newSubExprs[1], Type.Bool));
    }

    ////////////////////////////////////////////////////////////////////////////
    
    public override VCExpr! Visit(VCExprVar! node, VariableBindings! bindings) {
      VCExprVar res;
      if (!bindings.VCExprVarBindings.TryGetValue(node, out res))
        return AxBuilder.Typed2Untyped(node);
      return (!)res;
    }

    ////////////////////////////////////////////////////////////////////////////

    protected bool IsUniversalQuantifier(VCExprQuantifier! node) {
      return Polarity == 1 && node.Quan == Quantifier.EX ||
             Polarity == -1 && node.Quan == Quantifier.ALL;
    }

    protected List<VCExprVar!>! BoundVarsAfterErasure(List<VCExprVar!>! oldBoundVars,
                                                      // the mapping between old and new variables
                                                      // is added to this bindings-object
                                                      VariableBindings! bindings) {
      List<VCExprVar!>! newBoundVars = new List<VCExprVar!> (oldBoundVars.Count);
      foreach (VCExprVar! var in oldBoundVars) {
        Type! newType = AxBuilder.TypeAfterErasure(var.Type);
        VCExprVar! newVar = Gen.Variable(var.Name, newType);
        newBoundVars.Add(newVar);
        bindings.VCExprVarBindings.Add(var, newVar);
      }
      return newBoundVars;
    }

    // We check whether casts Int2U or Bool2U on the bound variables
    // occur in triggers. In case a trigger like f(Int2U(x)) occurs,
    // it may be better to give variable x the type U and remove the
    // cast. The following method returns true if the quantifier
    // should be translated again with a different typing
    protected bool RedoQuantifier(VCExprQuantifier! node,
                                  VCExprQuantifier! newNode,
                                  // the bound vars that actually occur in the body or
                                  // in any of the triggers
                                  List<VCExprVar!>! occurringVars,
                                  VariableBindings! oldBindings,
                                  out VariableBindings! newBindings,
                                  out List<VCExprVar!>! newBoundVars) {
      List<VCExprVar!> castVariables =
        VariableCastCollector.FindCastVariables(node, newNode, AxBuilder);
      if (castVariables.Count == 0) {
        newBindings = oldBindings;         // to make the compiler happy
        newBoundVars = newNode.BoundVars;  // to make the compiler happy
        return false;
      }

      // redo everything with a different typing ...

      newBindings = oldBindings.Clone();
      newBoundVars = new List<VCExprVar!> (node.BoundVars.Count);
      foreach (VCExprVar! var in node.BoundVars) {
        Type! newType =
          castVariables.Contains(var) ? AxBuilder.U
                                      : AxBuilder.TypeAfterErasure(var.Type);
        VCExprVar! newVar = Gen.Variable(var.Name, newType);
        newBoundVars.Add(newVar);
        newBindings.VCExprVarBindings.Add(var, newVar);
      }

      return true;
    }    

    ////////////////////////////////////////////////////////////////////////////

    public override VCExpr! Visit(VCExprLet! node, VariableBindings! bindings) {
      VariableBindings! newVarBindings = bindings.Clone();

      List<VCExprVar!>! newBoundVars = new List<VCExprVar!> (node.BoundVars.Count);
      foreach (VCExprVar! var in node.BoundVars) {
        Type! newType = AxBuilder.TypeAfterErasure(var.Type);
        VCExprVar! newVar = Gen.Variable(var.Name, newType);
        newBoundVars.Add(newVar);
        newVarBindings.VCExprVarBindings.Add(var, newVar);
      }

      List<VCExprLetBinding!>! newbindings = new List<VCExprLetBinding!> (node.Length);
      for (int i = 0; i < node.Length; ++i) {
        VCExprLetBinding! binding = node[i];
        VCExprVar! newVar = newBoundVars[i];
        Type! newType = newVar.Type;

        VCExpr! newE = AxBuilder.Cast(Mutate(binding.E, newVarBindings), newType);
        newbindings.Add(Gen.LetBinding(newVar, newE));
      }

      VCExpr! newbody = Mutate(node.Body, newVarBindings);
      return Gen.Let(newbindings, newbody);
    }
  }

  //////////////////////////////////////////////////////////////////////////////

  public abstract class OpTypeEraser : StandardVCExprOpVisitor<VCExpr!, VariableBindings!> {

    protected readonly TypeAxiomBuilderIntBoolU! AxBuilder;

    protected readonly TypeEraser! Eraser;
    protected readonly VCExpressionGenerator! Gen;

    public OpTypeEraser(TypeEraser! eraser, TypeAxiomBuilderIntBoolU! axBuilder,
                        VCExpressionGenerator! gen) {
      this.AxBuilder = axBuilder;
      this.Eraser = eraser;
      this.Gen = gen;
    }

    protected override VCExpr! StandardResult(VCExprNAry! node, VariableBindings! bindings) {
      System.Diagnostics.Debug.Fail("Don't know how to erase types in this expression: " + node);
      assert false;  // to please the compiler
    }

    private List<VCExpr!>! MutateSeq(VCExprNAry! node, VariableBindings! bindings,
                                     int newPolarity) {
      int oldPolarity = Eraser.Polarity;
      Eraser.Polarity = newPolarity;
      List<VCExpr!>! newArgs = Eraser.MutateSeq(node, bindings);
      Eraser.Polarity = oldPolarity;
      return newArgs;
    }

    private VCExpr! CastArguments(VCExprNAry! node, Type! argType, VariableBindings! bindings,
                                  int newPolarity) {
      return Gen.Function(node.Op,
                          AxBuilder.CastSeq(MutateSeq(node, bindings, newPolarity),
                          argType));
    }

    // Cast the arguments of the node to their old type if necessary and possible; otherwise use
    // their new type (int, bool, or U)
    private VCExpr! CastArgumentsToOldType(VCExprNAry! node, VariableBindings! bindings,
                                           int newPolarity)
      requires node.Arity > 0; {

      List<VCExpr!>! newArgs = MutateSeq(node, bindings, newPolarity);
      Type! oldType = node[0].Type;
      if (AxBuilder.UnchangedType(oldType) &&
          forall{int i in (1:node.Arity); node[i].Type.Equals(oldType)})
        return Gen.Function(node.Op, AxBuilder.CastSeq(newArgs, oldType));
      else
        return Gen.Function(node.Op, AxBuilder.CastSeq(newArgs, AxBuilder.U));
    }

    ///////////////////////////////////////////////////////////////////////////

    public override VCExpr! VisitNotOp      (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Bool, bindings, -Eraser.Polarity);
    }
    public override VCExpr! VisitEqOp       (VCExprNAry! node, VariableBindings! bindings) {
      return CastArgumentsToOldType(node, bindings, 0);
    }
    public override VCExpr! VisitNeqOp      (VCExprNAry! node, VariableBindings! bindings) {
      return CastArgumentsToOldType(node, bindings, 0);
    }
    public override VCExpr! VisitImpliesOp  (VCExprNAry! node, VariableBindings! bindings) {
      // UGLY: the code for tracking polarities should be factored out
      List<VCExpr!>! newArgs = new List<VCExpr!> (2);
      Eraser.Polarity = -Eraser.Polarity;
      newArgs.Add(Eraser.Mutate(node[0], bindings));
      Eraser.Polarity = -Eraser.Polarity;
      newArgs.Add(Eraser.Mutate(node[1], bindings));
      return Gen.Function(node.Op, AxBuilder.CastSeq(newArgs, Type.Bool));
    }
    public override VCExpr! VisitDistinctOp (VCExprNAry! node, VariableBindings! bindings) {
      return CastArgumentsToOldType(node, bindings, 0);
    }
    public override VCExpr! VisitLabelOp    (VCExprNAry! node, VariableBindings! bindings) {
      // argument of the label operator should always be a formula
      // (at least for Simplify ... should this be ensured at a later point?)
      return CastArguments(node, Type.Bool, bindings, Eraser.Polarity);
    }
    public override VCExpr! VisitIfThenElseOp  (VCExprNAry! node, VariableBindings! bindings) {
      List<VCExpr!>! newArgs = MutateSeq(node, bindings, 0);
      newArgs[0] = AxBuilder.Cast(newArgs[0], Type.Bool);
      Type t = node.Type;
      if (!AxBuilder.UnchangedType(t)) {
        t = AxBuilder.U;
      }
      newArgs[1] = AxBuilder.Cast(newArgs[1], t);
      newArgs[2] = AxBuilder.Cast(newArgs[2], t);
      return Gen.Function(node.Op, newArgs);
    }
    public override VCExpr! VisitCustomOp         (VCExprNAry! node, VariableBindings! bindings) {
      List<VCExpr!>! newArgs = MutateSeq(node, bindings, 0);
      return Gen.Function(node.Op, newArgs);
    }
    public override VCExpr! VisitAddOp            (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitSubOp            (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitMulOp            (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitDivOp            (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitModOp            (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitLtOp             (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitLeOp             (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitGtOp             (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitGeOp             (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, Type.Int, bindings, 0);
    }
    public override VCExpr! VisitSubtypeOp        (VCExprNAry! node, VariableBindings! bindings) {
      return CastArguments(node, AxBuilder.U, bindings, 0);
    }
    public override VCExpr! VisitBvOp       (VCExprNAry! node, VariableBindings! bindings) {
      return CastArgumentsToOldType(node, bindings, 0);
    }
    public override VCExpr! VisitBvExtractOp(VCExprNAry! node, VariableBindings! bindings) {
      return CastArgumentsToOldType(node, bindings, 0);
    }
    public override VCExpr! VisitBvConcatOp (VCExprNAry! node, VariableBindings! bindings) {
      List<VCExpr!>! newArgs = MutateSeq(node, bindings, 0);

      // each argument is cast to its old type
      assert newArgs.Count == node.Arity && newArgs.Count == 2;
      VCExpr! arg0 = AxBuilder.Cast(newArgs[0], node[0].Type);
      VCExpr! arg1 = AxBuilder.Cast(newArgs[1], node[1].Type);

      return Gen.Function(node.Op, arg0, arg1);
    }

  }

  //////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// Collect all variables x occurring in expressions of the form Int2U(x) or Bool2U(x), and
  /// collect all variables x occurring outside such forms.
  /// </summary>
  internal class VariableCastCollector : TraversingVCExprVisitor<bool, bool> {
    /// <summary>
    /// Determine those bound variables in "oldNode" <em>all</em> of whose relevant uses
    /// have to be cast in potential triggers in "newNode".  It is assume that
    /// the bound variables of "oldNode" correspond to the first bound
    /// variables of "newNode".
    /// </summary>
    public static List<VCExprVar!>! FindCastVariables(VCExprQuantifier! oldNode,
                                                      VCExprQuantifier! newNode,
                                                      TypeAxiomBuilderIntBoolU! axBuilder) {
      VariableCastCollector! collector = new VariableCastCollector(axBuilder);
      if (exists{VCTrigger! trigger in newNode.Triggers; trigger.Pos}) {
        // look in the given triggers
        foreach (VCTrigger! trigger in newNode.Triggers)
          if (trigger.Pos)
            foreach (VCExpr! expr in trigger.Exprs)
              collector.Traverse(expr, true);
      } else {
        // look in the body of the quantifier
        collector.Traverse(newNode.Body, true);
      }

      List<VCExprVar!>! castVariables = new List<VCExprVar!> (collector.varsInCasts.Count);
      foreach (VCExprVar! castVar in collector.varsInCasts) {
        int i = newNode.BoundVars.IndexOf(castVar);
        if (0 <= i && i < oldNode.BoundVars.Count && !collector.varsOutsideCasts.ContainsKey(castVar))
          castVariables.Add(oldNode.BoundVars[i]);
      }
      return castVariables;
    }

    public VariableCastCollector(TypeAxiomBuilderIntBoolU! axBuilder) {
      this.AxBuilder = axBuilder;
    }

    readonly List<VCExprVar!>! varsInCasts = new List<VCExprVar!> ();
    readonly Dictionary<VCExprVar!,object>! varsOutsideCasts = new Dictionary<VCExprVar!,object> ();

    readonly TypeAxiomBuilderIntBoolU! AxBuilder;

    protected override bool StandardResult(VCExpr! node, bool arg) {
      return true; // not used
    }

    public override bool Visit(VCExprNAry! node, bool arg) {
      if (node.Op is VCExprBoogieFunctionOp) {
        Function! func = ((VCExprBoogieFunctionOp)node.Op).Func;
        if ((AxBuilder.IsCast(func)) && node[0] is VCExprVar) {
          VCExprVar castVar = (VCExprVar)node[0];
          if (!varsInCasts.Contains(castVar))
            varsInCasts.Add(castVar);
          return true;
        }
      } else if (node.Op is VCExprNAryOp) {
        VCExpressionGenerator.SingletonOp op = VCExpressionGenerator.SingletonOpDict[node.Op];
        switch(op) {
          // the following operators cannot be used in triggers, so disregard any uses of variables as direct arguments
          case VCExpressionGenerator.SingletonOp.NotOp:
          case VCExpressionGenerator.SingletonOp.EqOp:
          case VCExpressionGenerator.SingletonOp.NeqOp:
          case VCExpressionGenerator.SingletonOp.AndOp:
          case VCExpressionGenerator.SingletonOp.OrOp:
          case VCExpressionGenerator.SingletonOp.ImpliesOp:
          case VCExpressionGenerator.SingletonOp.LtOp:
          case VCExpressionGenerator.SingletonOp.LeOp:
          case VCExpressionGenerator.SingletonOp.GtOp:
          case VCExpressionGenerator.SingletonOp.GeOp:
            foreach (VCExpr n in node) {
              if (!(n is VCExprVar)) {  // don't recurse on VCExprVar argument
                n.Accept<bool,bool>(this, arg);
              }
            }
            return true;
          default:
            break;
        }
      }
      return base.Visit(node, arg);
    }
    
    public override bool Visit(VCExprVar! node, bool arg) {
      if (!varsOutsideCasts.ContainsKey(node))
        varsOutsideCasts.Add(node, null);
      return true;
    }
  }

}