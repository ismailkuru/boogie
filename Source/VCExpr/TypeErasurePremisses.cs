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

// Erasure of types using premisses   (forall x :: type(x)=T ==> p(x))

namespace Microsoft.Boogie.TypeErasure
{
  using Microsoft.Boogie.VCExprAST;

  // When using type premisses, we can distinguish two kinds of type
  // parameters of a function or map: parameters that occur in the
  // formal argument types of the function are "implicit" because they
  // can be inferred from the actual argument types; parameters that
  // only occur in the result type of the function are "explicit"
  // because they are not inferrable and have to be given to the
  // function as additional arguments.
  //
  // The following structure is used to store the untyped version of a
  // typed function, together with the lists of implicit and explicit
  // type parameters (in the same order as they occur in the signature
  // of the original function).

  internal struct UntypedFunction {
    public readonly Function! Fun;
    // type parameters that can be extracted from the value parameters
    public readonly List<TypeVariable!>! ImplicitTypeParams;
    // type parameters that have to be given explicitly
    public readonly List<TypeVariable!>! ExplicitTypeParams;

    public UntypedFunction(Function! fun,
                           List<TypeVariable!>! implicitTypeParams,
                           List<TypeVariable!>! explicitTypeParams) {
      Fun = fun;
      ImplicitTypeParams = implicitTypeParams;
      ExplicitTypeParams = explicitTypeParams;
    }
  }

  public class TypeAxiomBuilderPremisses : TypeAxiomBuilderIntBoolU {

    public TypeAxiomBuilderPremisses(VCExpressionGenerator! gen) {
      base(gen);
      TypeFunction = HelperFuns.BoogieFunction("dummy", Type.Int);
      Typed2UntypedFunctions = new Dictionary<Function!, UntypedFunction> ();
      MapTypeAbstracterAttr = null;
    }

    // constructor to allow cloning
    [NotDelayed]
    internal TypeAxiomBuilderPremisses(TypeAxiomBuilderPremisses! builder) {
      TypeFunction = builder.TypeFunction;
      Typed2UntypedFunctions =
        new Dictionary<Function!, UntypedFunction> (builder.Typed2UntypedFunctions);
      base(builder);

      MapTypeAbstracterAttr =
        builder.MapTypeAbstracterAttr == null ?
        null : new MapTypeAbstractionBuilderPremisses(this, builder.Gen,
                                                      builder.MapTypeAbstracterAttr);
    }

    public override Object! Clone() {
      return new TypeAxiomBuilderPremisses(this);
    }

    public override void Setup() {
      TypeFunction = HelperFuns.BoogieFunction("type", U, T);
      base.Setup();
    }

    ////////////////////////////////////////////////////////////////////////////

    // generate axioms of the kind "forall x:U. {Int2U(U2Int(x))}
    //                                          type(x)=int ==> Int2U(U2Int(x))==x"
    protected override VCExpr! GenReverseCastAxiom(Function! castToU, Function! castFromU) {
      List<VCTrigger!>! triggers;
      VCExprVar! var;
      VCExpr! eq = GenReverseCastEq(castToU, castFromU, out var, out triggers);
      VCExpr! premiss;
      if (CommandLineOptions.Clo.TypeEncodingMethod
              == CommandLineOptions.TypeEncoding.None)
        premiss = VCExpressionGenerator.True;
      else
        premiss = GenVarTypeAxiom(var, ((!)castFromU.OutParams[0]).TypedIdent.Type,
                                  // we don't have any bindings available
                                  new Dictionary<TypeVariable!, VCExpr!> ());
      VCExpr! matrix = Gen.ImpliesSimp(premiss, eq);
      return Gen.Forall(HelperFuns.ToList(var), triggers, "cast:" + castFromU.Name, matrix);
    }

    protected override VCExpr! GenCastTypeAxioms(Function! castToU, Function! castFromU) {
      Type! fromType = ((!)castToU.InParams[0]).TypedIdent.Type;
      return GenFunctionAxiom(castToU, new List<TypeVariable!> (), new List<TypeVariable!> (),
                              HelperFuns.ToList(fromType), fromType);
    }

    private MapTypeAbstractionBuilderPremisses MapTypeAbstracterAttr;

    internal override MapTypeAbstractionBuilder! MapTypeAbstracter { get {
      if (MapTypeAbstracterAttr == null)
        MapTypeAbstracterAttr = new MapTypeAbstractionBuilderPremisses (this, Gen);
      return MapTypeAbstracterAttr;
    } }

    internal MapTypeAbstractionBuilderPremisses! MapTypeAbstracterPremisses { get {
      return (MapTypeAbstractionBuilderPremisses)MapTypeAbstracter;
    } }

    ////////////////////////////////////////////////////////////////////////////

    // function that maps individuals to their type
    // the field is overwritten with its actual value in "Setup"
    private Function! TypeFunction;

    public VCExpr! TypeOf(VCExpr! expr) {
      return Gen.Function(TypeFunction, expr);
    }

    ///////////////////////////////////////////////////////////////////////////
    // Generate type premisses and type parameter bindings for quantifiers, functions, procedures

    // let-bindings to extract the instantiations of type parameters
    public List<VCExprLetBinding!>!
           GenTypeParamBindings(// the original bound variables and (implicit) type parameters
                                List<TypeVariable!>! typeParams, List<VCExprVar!>! oldBoundVars,
                                // VariableBindings to which the translation
                                // TypeVariable -> VCExprVar is added
                                VariableBindings! bindings,
                                bool addTypeVarsToBindings) {
      // type variables are replaced with ordinary variables that are bound using a
      // let-expression
      if (addTypeVarsToBindings) {
        foreach (TypeVariable! tvar in typeParams)
          bindings.TypeVariableBindings.Add(tvar, Gen.Variable(tvar.Name, T));
      }

      // extract the values of type variables from the term variables
      List<VCExprVar!>! UtypedVars = new List<VCExprVar!> (oldBoundVars.Count);
      List<Type!>! originalTypes = new List<Type!> (oldBoundVars.Count);
      foreach (VCExprVar var in oldBoundVars) {
        VCExprVar! newVar = bindings.VCExprVarBindings[var];
        if (newVar.Type.Equals(U)) {
          UtypedVars.Add(newVar);
          originalTypes.Add(var.Type);
        }
      }
      
      UtypedVars.TrimExcess();
      originalTypes.TrimExcess();

      return BestTypeVarExtractors(typeParams, originalTypes, UtypedVars, bindings);
    }


    public VCExpr! AddTypePremisses(List<VCExprLetBinding!>! typeVarBindings,
                                    VCExpr! typePremisses, bool universal,
                                    VCExpr! body) {
      VCExpr! bodyWithPremisses;
      if (universal)
        bodyWithPremisses = Gen.ImpliesSimp(typePremisses, body);
      else
        bodyWithPremisses = Gen.AndSimp(typePremisses, body);

      return Gen.Let(typeVarBindings, bodyWithPremisses);
    }


    ///////////////////////////////////////////////////////////////////////////
    // Extract the instantiations of type variables from the concrete types of
    // term variables. E.g., for a function  f<a>(x : C a), we would extract the
    // instantiation of "a" by looking at the concrete type of "x".

    public List<VCExprLetBinding!>!
           BestTypeVarExtractors(List<TypeVariable!>! vars, List<Type!>! types,
                                 List<VCExprVar!>! concreteTypeSources,
                                 VariableBindings! bindings) {
      List<VCExprLetBinding!>! typeParamBindings = new List<VCExprLetBinding!> ();
      foreach (TypeVariable! var in vars) {
        VCExpr extractor = BestTypeVarExtractor(var, types, concreteTypeSources);
        if (extractor != null)
          typeParamBindings.Add(
            Gen.LetBinding((VCExprVar)bindings.TypeVariableBindings[var],
                           extractor));
      }
      return typeParamBindings;
    }

    private VCExpr BestTypeVarExtractor(TypeVariable! var, List<Type!>! types,
                                        List<VCExprVar!>! concreteTypeSources) {
      List<VCExpr!> allExtractors = TypeVarExtractors(var, types, concreteTypeSources);
      if (allExtractors.Count == 0)
        return null;

      VCExpr bestExtractor = allExtractors[0];
      int bestExtractorSize = SizeComputingVisitor.ComputeSize(bestExtractor);
      for (int i = 1; i < allExtractors.Count; ++i) {
        int newSize = SizeComputingVisitor.ComputeSize(allExtractors[i]);
        if (newSize < bestExtractorSize) {
          bestExtractor = allExtractors[i];
          bestExtractorSize = newSize;
        }
      }

      return bestExtractor;
    }

    private List<VCExpr!>! TypeVarExtractors(TypeVariable! var, List<Type!>! types,
                                             List<VCExprVar!>! concreteTypeSources)
      requires types.Count == concreteTypeSources.Count; {
      List<VCExpr!>! res = new List<VCExpr!>();
      for (int i = 0; i < types.Count; ++i)
        TypeVarExtractors(var, types[i], TypeOf(concreteTypeSources[i]), res);

      return res;
    }

    private void TypeVarExtractors(TypeVariable! var, Type! completeType,
                                   VCExpr! innerTerm, List<VCExpr!>! extractors) {
      if (completeType.IsVariable) {
        if (var.Equals(completeType)) {
          extractors.Add(innerTerm);
        }  // else nothing
      } else if (completeType.IsBasic) {
        // nothing
      } else if (completeType.IsCtor) {
        CtorType! ctorType = completeType.AsCtor;
        if (ctorType.Arguments.Length > 0) {
          // otherwise there are no chances of extracting any
          // instantiations from this type
          TypeCtorRepr repr = GetTypeCtorReprStruct(ctorType.Decl);
          for (int i = 0; i < ctorType.Arguments.Length; ++i) {
            VCExpr! newInnerTerm = Gen.Function(repr.Dtors[i], innerTerm);
            TypeVarExtractors(var, ctorType.Arguments[i], newInnerTerm, extractors);
          }
        }
      } else if (completeType.IsMap) {
        TypeVarExtractors(var, MapTypeAbstracter.AbstractMapType(completeType.AsMap),
                          innerTerm, extractors);
      } else {
        System.Diagnostics.Debug.Fail("Don't know how to handle this type: " + completeType);
      }
    }

    ////////////////////////////////////////////////////////////////////////////
    // Symbols for representing functions

    // Globally defined functions
    private readonly IDictionary<Function!, UntypedFunction>! Typed2UntypedFunctions;

    // distinguish between implicit and explicit type parameters
    internal static void SeparateTypeParams(List<Type!>! valueArgumentTypes,
                                            TypeVariableSeq! allTypeParams,
                                            out List<TypeVariable!>! implicitParams,
                                            out List<TypeVariable!>! explicitParams) {
        TypeVariableSeq! varsInInParamTypes = new TypeVariableSeq ();
        foreach (Type! t in valueArgumentTypes)
          varsInInParamTypes.AppendWithoutDups(t.FreeVariables);

        implicitParams = new List<TypeVariable!> (allTypeParams.Length);
        explicitParams = new List<TypeVariable!> (allTypeParams.Length);

        foreach (TypeVariable! var in allTypeParams) {
          if (varsInInParamTypes.Has(var))
            implicitParams.Add(var);
          else
            explicitParams.Add(var);
        }
        
        implicitParams.TrimExcess();
        explicitParams.TrimExcess();
    }

    internal UntypedFunction Typed2Untyped(Function! fun) {
      UntypedFunction res;
      if (!Typed2UntypedFunctions.TryGetValue(fun, out res)) {
        assert fun.OutParams.Length == 1;

        // if all of the parameters are int or bool, the function does
        // not have to be changed
        if (forall{Formal f in fun.InParams; UnchangedType(((!)f).TypedIdent.Type)} &&
            UnchangedType(((!)fun.OutParams[0]).TypedIdent.Type) &&
            fun.TypeParameters.Length == 0) {
          res = new UntypedFunction(fun, new List<TypeVariable!> (), new List<TypeVariable!> ());
        } else {
          List<Type!>! argTypes = new List<Type!> ();
          foreach (Variable! v in fun.InParams)
            argTypes.Add(v.TypedIdent.Type);

          List<TypeVariable!>! implicitParams, explicitParams;
          SeparateTypeParams(argTypes, fun.TypeParameters, out implicitParams, out explicitParams);

          Type[]! types = new Type [explicitParams.Count + fun.InParams.Length + 1];
          int i = 0;
          for (int j = 0; j < explicitParams.Count; ++j) {
            types[i] = T;
            i = i + 1;
          }
          for (int j = 0; j < fun.InParams.Length; ++i, ++j)
            types[i] = TypeAfterErasure(((!)fun.InParams[j]).TypedIdent.Type);
          types[types.Length - 1] = TypeAfterErasure(((!)fun.OutParams[0]).TypedIdent.Type);

          Function! untypedFun = HelperFuns.BoogieFunction(fun.Name, types);
          untypedFun.Attributes = fun.Attributes;
          res = new UntypedFunction(untypedFun, implicitParams, explicitParams);
          if (U.Equals(types[types.Length - 1]))
            AddTypeAxiom(GenFunctionAxiom(res, fun));
        }

        Typed2UntypedFunctions.Add(fun, res);
      }
      return res;
    }

    private VCExpr! GenFunctionAxiom(UntypedFunction fun, Function! originalFun) {
      List<Type!>! originalInTypes = new List<Type!> (originalFun.InParams.Length);
      foreach (Formal! f in originalFun.InParams)
        originalInTypes.Add(f.TypedIdent.Type);

      return GenFunctionAxiom(fun.Fun, fun.ImplicitTypeParams, fun.ExplicitTypeParams,
                              originalInTypes,
                              ((!)originalFun.OutParams[0]).TypedIdent.Type);
    }

    internal VCExpr! GenFunctionAxiom(Function! fun,
                                      List<TypeVariable!>! implicitTypeParams,
                                      List<TypeVariable!>! explicitTypeParams,
                                      List<Type!>! originalInTypes,
                                      Type! originalResultType)
      requires originalInTypes.Count + explicitTypeParams.Count == fun.InParams.Length;
    {
      if (CommandLineOptions.Clo.TypeEncodingMethod == CommandLineOptions.TypeEncoding.None) {
        return VCExpressionGenerator.True;
      }                

      List<VCExprVar!>! typedInputVars = new List<VCExprVar!>(originalInTypes.Count);
      int i = 0;
      foreach (Type! t in originalInTypes) {
        typedInputVars.Add(Gen.Variable("arg" + i, t));
        i = i + 1;
      }

      VariableBindings! bindings = new VariableBindings ();

      // type parameters that have to be given explicitly are replaced
      // with universally quantified type variables
      List<VCExprVar!>! boundVars = new List<VCExprVar!> (explicitTypeParams.Count + typedInputVars.Count);
      foreach (TypeVariable! var in explicitTypeParams) {
        VCExprVar! newVar = Gen.Variable(var.Name, T);
        boundVars.Add(newVar);
        bindings.TypeVariableBindings.Add(var, newVar);
      }

      // bound term variables are replaced with bound term variables typed in
      // a simpler way
      foreach (VCExprVar! var in typedInputVars) {
        Type! newType = TypeAfterErasure(var.Type);
        VCExprVar! newVar = Gen.Variable(var.Name, newType);
        boundVars.Add(newVar);
        bindings.VCExprVarBindings.Add(var, newVar);
      }

      List<VCExprLetBinding!> typeVarBindings =
        GenTypeParamBindings(implicitTypeParams, typedInputVars, bindings, true);

      VCExpr! funApp = Gen.Function(fun, HelperFuns.ToVCExprList(boundVars));
      VCExpr! conclusion = Gen.Eq(TypeOf(funApp),
                                  Type2Term(originalResultType, bindings.TypeVariableBindings));
      VCExpr conclusionWithPremisses =
        // leave out antecedents of function type axioms ... they don't appear necessary,
        // because a function can always be extended to all U-values (right?)
//        AddTypePremisses(typeVarBindings, typePremisses, true, conclusion);
        Gen.Let(typeVarBindings, conclusion);

      if (boundVars.Count > 0) {
        List<VCTrigger!> triggers = HelperFuns.ToList(Gen.Trigger(true, HelperFuns.ToList(funApp)));
        return Gen.Forall(boundVars, triggers, "funType:" + fun.Name, conclusionWithPremisses);
      } else {
        return conclusionWithPremisses;
      }
    }
    
    ////////////////////////////////////////////////////////////////////////////

    protected override void AddVarTypeAxiom(VCExprVar! var, Type! originalType) {
      if (CommandLineOptions.Clo.TypeEncodingMethod == CommandLineOptions.TypeEncoding.None) return;
      AddTypeAxiom(GenVarTypeAxiom(var, originalType,
                                   // we don't have any bindings available
                                   new Dictionary<TypeVariable!, VCExpr!> ()));
    }

    public VCExpr! GenVarTypeAxiom(VCExprVar! var, Type! originalType,
                                   IDictionary<TypeVariable!, VCExpr!>! varMapping) {
      if (!var.Type.Equals(originalType)) {
        VCExpr! typeRepr = Type2Term(originalType, varMapping);
        return Gen.Eq(TypeOf(var), typeRepr);
      }
      return VCExpressionGenerator.True;
    }
  }

  /////////////////////////////////////////////////////////////////////////////

  internal class MapTypeAbstractionBuilderPremisses : MapTypeAbstractionBuilder {

    private readonly TypeAxiomBuilderPremisses! AxBuilderPremisses;

    internal MapTypeAbstractionBuilderPremisses(TypeAxiomBuilderPremisses! axBuilder,
                                                VCExpressionGenerator! gen) {
      base(axBuilder, gen);
      this.AxBuilderPremisses = axBuilder;
    }

    // constructor for cloning
    internal MapTypeAbstractionBuilderPremisses(TypeAxiomBuilderPremisses! axBuilder,
                                                VCExpressionGenerator! gen,
                                                MapTypeAbstractionBuilderPremisses! builder) {
      base(axBuilder, gen, builder);
      this.AxBuilderPremisses = axBuilder;
    }

    ////////////////////////////////////////////////////////////////////////////

    // Determine the type parameters of a map type that have to be
    // given explicitly when applying the select function (the
    // parameters that only occur in the result type of the
    // map). These parameters are given as a list of indexes sorted in
    // ascending order; the index i refers to the i'th bound variable
    // in a type    <a0, a1, ..., an>[...]...
    public List<int>! ExplicitSelectTypeParams(MapType! type) {
      List<int> res;
      if (!explicitSelectTypeParamsCache.TryGetValue(type, out res)) {
        List<TypeVariable!>! explicitParams, implicitParams;
        TypeAxiomBuilderPremisses.SeparateTypeParams(type.Arguments.ToList(),
                                                     type.TypeParameters,
                                                     out implicitParams,
                                                     out explicitParams);
        res = new List<int> (explicitParams.Count);
        foreach (TypeVariable! var in explicitParams)
          res.Add(type.TypeParameters.IndexOf(var));
        explicitSelectTypeParamsCache.Add(type, res);
      }
      return (!)res;
    }

    private IDictionary<MapType!, List<int>!>! explicitSelectTypeParamsCache =
      new Dictionary<MapType!, List<int>!> ();

    ////////////////////////////////////////////////////////////////////////////

    protected override void GenSelectStoreFunctions(MapType! abstractedType,
                                                    TypeCtorDecl! synonym,
                                                    out Function! select,
                                                    out Function! store) {
      Type! mapTypeSynonym;
      List<TypeVariable!>! typeParams;
      List<Type!>! originalInTypes;
      GenTypeAxiomParams(abstractedType, synonym, out mapTypeSynonym,
                         out typeParams, out originalInTypes);

      // select
      List<TypeVariable!>! explicitSelectParams, implicitSelectParams;
      select = CreateAccessFun(typeParams, originalInTypes,
                               abstractedType.Result, synonym.Name + "Select",
                               out implicitSelectParams, out explicitSelectParams);
      
      // store, which gets one further argument: the assigned rhs
      originalInTypes.Add(abstractedType.Result);

      List<TypeVariable!>! explicitStoreParams, implicitStoreParams;
      store = CreateAccessFun(typeParams, originalInTypes,
                              mapTypeSynonym, synonym.Name + "Store",
                              out implicitStoreParams, out explicitStoreParams);

	  // the store function does not have any explicit type parameters
      assert explicitStoreParams.Count == 0;
      
      if (CommandLineOptions.Clo.UseArrayTheory) {
        select.AddAttribute("builtin", "select");
        store.AddAttribute("builtin", "store");
      } else {                       
        AxBuilder.AddTypeAxiom(GenMapAxiom0(select, store,
                                            abstractedType.Result,
                                            implicitSelectParams, explicitSelectParams,
                                            originalInTypes));
        AxBuilder.AddTypeAxiom(GenMapAxiom1(select, store, 
                                            abstractedType.Result,
                                            explicitSelectParams));
      }
    }

    protected void GenTypeAxiomParams(MapType! abstractedType, TypeCtorDecl! synonymDecl,
                                      out Type! mapTypeSynonym,
                                      out List<TypeVariable!>! typeParams,
                                      out List<Type!>! originalIndexTypes) {
      typeParams = new List<TypeVariable!> (abstractedType.TypeParameters.Length + abstractedType.FreeVariables.Length);
      typeParams.AddRange(abstractedType.TypeParameters.ToList());
      typeParams.AddRange(abstractedType.FreeVariables.ToList());

      originalIndexTypes = new List<Type!> (abstractedType.Arguments.Length + 1);
      TypeSeq! mapTypeParams = new TypeSeq ();
      foreach (TypeVariable! var in abstractedType.FreeVariables)
        mapTypeParams.Add(var);
      
      if (CommandLineOptions.Clo.UseArrayTheory)
        mapTypeSynonym = abstractedType;
      else 
        mapTypeSynonym = new CtorType (Token.NoToken, synonymDecl, mapTypeParams);
        
      originalIndexTypes.Add(mapTypeSynonym);
      originalIndexTypes.AddRange(abstractedType.Arguments.ToList());
    }

    // method to actually create the select or store function
    private Function! CreateAccessFun(List<TypeVariable!>! originalTypeParams,
                                      List<Type!>! originalInTypes,
                                      Type! originalResult,
                                      string! name,
                                      out List<TypeVariable!>! implicitTypeParams, out List<TypeVariable!>! explicitTypeParams) {
      // select and store are basically handled like normal functions: the type
      // parameters are split into the implicit parameters, and into the parameters
      // that have to be given explicitly
      TypeAxiomBuilderPremisses.SeparateTypeParams(originalInTypes,
                                                   HelperFuns.ToSeq(originalTypeParams),
                                                   out implicitTypeParams,
                                                   out explicitTypeParams);
      
      Type[]! ioTypes = new Type [explicitTypeParams.Count + originalInTypes.Count + 1];
      int i = 0;
      for (; i < explicitTypeParams.Count; ++i)
        ioTypes[i] = AxBuilder.T;
      foreach (Type! type in originalInTypes)
      {
        if (CommandLineOptions.Clo.Monomorphize && AxBuilder.UnchangedType(type))
            ioTypes[i] = type;
        else
            ioTypes[i] = AxBuilder.U;
        i++;
      }
      if (CommandLineOptions.Clo.Monomorphize && AxBuilder.UnchangedType(originalResult))
        ioTypes[i] = originalResult;
      else
        ioTypes[i] = AxBuilder.U;
        
      Function! res = HelperFuns.BoogieFunction(name, ioTypes);

      if (AxBuilder.U.Equals(ioTypes[i]))
      {
        AxBuilder.AddTypeAxiom(
            AxBuilderPremisses.GenFunctionAxiom(res,
                                            implicitTypeParams, explicitTypeParams,
                                            originalInTypes, originalResult));
      }
      return res;
    }

    ///////////////////////////////////////////////////////////////////////////
    // The normal axioms of the theory of arrays (without extensionality)

    private VCExpr! Select(Function! select,
                           // in general, the select function has to
                           // receive explicit type parameters (which
                           // are here already represented as VCExpr
                           // of type T)
                           List<VCExpr!>! typeParams,
                           VCExpr! map,
                           List<VCExprVar!>! indexes) {
      List<VCExpr!>! selectArgs = new List<VCExpr!> (typeParams.Count + indexes.Count + 1);
      selectArgs.AddRange(typeParams);
      selectArgs.Add(map);
      selectArgs.AddRange(HelperFuns.ToVCExprList(indexes));
      return Gen.Function(select, selectArgs);
    }

    private VCExpr! Store(Function! store,
                          VCExpr! map,
                          List<VCExprVar!>! indexes,
                          VCExpr! val) {
      List<VCExpr!>! storeArgs = new List<VCExpr!> (indexes.Count + 2);
      storeArgs.Add(map);
      storeArgs.AddRange(HelperFuns.ToVCExprList(indexes));
      storeArgs.Add(val);
      return Gen.Function(store, storeArgs);
    }

    /// <summary>
    /// Generate:
    ///   (forall m, indexes, val ::
    ///     type(val) == T ==>
    ///     select(store(m, indexes, val), indexes) == val)
    /// where the quantifier body is also enclosed in a let that defines portions of T, if needed.
    /// </summary>
    private VCExpr! GenMapAxiom0(Function! select, Function! store,
                                 Type! mapResult,
                                 List<TypeVariable!>! implicitTypeParamsSelect, List<TypeVariable!>! explicitTypeParamsSelect,
                                 List<Type!>! originalInTypes)
    {
      int arity = store.InParams.Length - 2;
      List<VCExprVar!> inParams = new List<VCExprVar!>();
      List<VCExprVar!> quantifiedVars = new List<VCExprVar!>(store.InParams.Length);
      VariableBindings bindings = new VariableBindings();

      // bound variable:  m
      VCExprVar typedM = Gen.Variable("m", originalInTypes[0]);
      VCExprVar m = Gen.Variable("m", AxBuilder.U);
      inParams.Add(typedM);
      quantifiedVars.Add(m);
      bindings.VCExprVarBindings.Add(typedM, m);

      // bound variables:  indexes
      List<Type!> origIndexTypes = new List<Type!>(arity);
      List<Type!> indexTypes = new List<Type!>(arity);
      for (int i = 1; i < store.InParams.Length-1; i++) {
        origIndexTypes.Add(originalInTypes[i]);
        indexTypes.Add(((!)store.InParams[i]).TypedIdent.Type);
      }
      assert arity == indexTypes.Count;
      List<VCExprVar!> typedArgs = HelperFuns.VarVector("arg", origIndexTypes, Gen);
      List<VCExprVar!> indexes = HelperFuns.VarVector("x", indexTypes, Gen);
      assert typedArgs.Count == indexes.Count;
      inParams.AddRange(typedArgs);
      quantifiedVars.AddRange(indexes);
      for (int i = 0; i < arity; i++) {
        bindings.VCExprVarBindings.Add(typedArgs[i], indexes[i]);
      }

      // bound variable:  val
      VCExprVar typedVal = Gen.Variable("val", mapResult);
      VCExprVar val = Gen.Variable("val", ((!)select.OutParams[0]).TypedIdent.Type);
      quantifiedVars.Add(val);
      bindings.VCExprVarBindings.Add(typedVal, val);

      // add all type parameters into bindings
      foreach (TypeVariable tp in implicitTypeParamsSelect) {
        VCExprVar tVar = Gen.Variable(tp.Name, AxBuilderPremisses.T);
        bindings.TypeVariableBindings.Add(tp, tVar);
      }
      List<VCExpr!> typeParams = new List<VCExpr!>(explicitTypeParamsSelect.Count);
      foreach (TypeVariable tp in explicitTypeParamsSelect) {
        VCExprVar tVar = Gen.Variable(tp.Name, AxBuilderPremisses.T);
        bindings.TypeVariableBindings.Add(tp, tVar);
        // ... and record these explicit type-parameter arguments in typeParams
        typeParams.Add(tVar);
      }

      VCExpr! storeExpr = Store(store, m, indexes, val);
      VCExpr! selectExpr = Select(select, typeParams, storeExpr, indexes);

      // Create let-binding definitions for all type parameters.
      // The implicit ones can be phrased in terms of the types of the ordinary in-parameters, and
      // we want to make sure that they don't get phrased in terms of the out-parameter, so we pass
      // in inParams here.
      List<VCExprLetBinding!> letBindings_Implicit =
        AxBuilderPremisses.GenTypeParamBindings(implicitTypeParamsSelect, inParams, bindings, false);
      // The explicit ones, by definition, can only be phrased in terms of the result, so we pass
      // in List(typedVal) here.
      List<VCExprLetBinding!> letBindings_Explicit =
        AxBuilderPremisses.GenTypeParamBindings(explicitTypeParamsSelect, HelperFuns.ToList(typedVal), bindings, false);

      // generate:  select(store(m, indices, val)) == val
      VCExpr! eq = Gen.Eq(selectExpr, val);
      // generate:  type(val) == T, where T is the type of val
      VCExpr! ante = Gen.Eq(
        AxBuilderPremisses.TypeOf(val),
        AxBuilderPremisses.Type2Term(mapResult, bindings.TypeVariableBindings));
      VCExpr body;
      if (CommandLineOptions.Clo.TypeEncodingMethod == CommandLineOptions.TypeEncoding.None ||
          !AxBuilder.U.Equals(((!)select.OutParams[0]).TypedIdent.Type))
      {
        body = Gen.Let(letBindings_Explicit, eq);
      } else {
        body = Gen.Let(letBindings_Implicit, Gen.Let(letBindings_Explicit, Gen.ImpliesSimp(ante, eq)));
      }
      return Gen.Forall(quantifiedVars, new List<VCTrigger!>(), "mapAx0:" + select.Name, body);
    }

    private VCExpr! GenMapAxiom1(Function! select, Function! store,
                                 Type! mapResult,
                                 List<TypeVariable!>! explicitSelectParams) {
      int arity = store.InParams.Length - 2;

      List<Type!> indexTypes = new List<Type!>();
      for (int i = 1; i < store.InParams.Length-1; i++)
      {
        indexTypes.Add(((!)store.InParams[i]).TypedIdent.Type);
      }
      assert indexTypes.Count == arity;
      
      List<VCExprVar!>! indexes0 = HelperFuns.VarVector("x", indexTypes, Gen);
      List<VCExprVar!>! indexes1 = HelperFuns.VarVector("y", indexTypes, Gen);
      VCExprVar! m = Gen.Variable("m", AxBuilder.U);
      VCExprVar! val = Gen.Variable("val", ((!)select.OutParams[0]).TypedIdent.Type);

      // extract the explicit type parameters from the actual result type ...
      VCExprVar! typedVal = Gen.Variable("val", mapResult);
      VariableBindings! bindings = new VariableBindings ();
      bindings.VCExprVarBindings.Add(typedVal, val);

      List<VCExprLetBinding!>! letBindings =
        AxBuilderPremisses.GenTypeParamBindings(explicitSelectParams,
                                                HelperFuns.ToList(typedVal),
                                                bindings, true);

      // ... and quantify the introduced term variables for type
      // parameters universally
      List<VCExprVar!>! typeParams = new List<VCExprVar!> (explicitSelectParams.Count);
      List<VCExpr!>! typeParamsExpr = new List<VCExpr!> (explicitSelectParams.Count);
      foreach (TypeVariable! var in explicitSelectParams) {
        VCExprVar! newVar = (VCExprVar)bindings.TypeVariableBindings[var];
        typeParams.Add(newVar);
        typeParamsExpr.Add(newVar);
      }

      VCExpr! storeExpr = Store(store, m, indexes0, val);
      VCExpr! selectWithoutStoreExpr = Select(select, typeParamsExpr, m, indexes1);
      VCExpr! selectExpr = Select(select, typeParamsExpr, storeExpr, indexes1);

      VCExpr! selectEq = Gen.Eq(selectExpr, selectWithoutStoreExpr);

      List<VCExprVar!>! quantifiedVars = new List<VCExprVar!> (indexes0.Count + indexes1.Count + 2);
      quantifiedVars.Add(val);
      quantifiedVars.Add(m);
      quantifiedVars.AddRange(indexes0);
      quantifiedVars.AddRange(indexes1);
      quantifiedVars.AddRange(typeParams);

      List<VCTrigger!>! triggers = new List<VCTrigger!> ();

      VCExpr! axiom = VCExpressionGenerator.True;

      // first non-interference criterium: the queried location is
      // different from the assigned location
      for (int i = 0; i < arity; ++i) {
        VCExpr! indexesEq = Gen.Eq(indexes0[i], indexes1[i]);
        VCExpr! matrix = Gen.Or(indexesEq, selectEq);
        VCExpr! conjunct = Gen.Forall(quantifiedVars, triggers,
                                      "mapAx1:" + select.Name + ":" + i, matrix);
        axiom = Gen.AndSimp(axiom, conjunct);
      }

      // second non-interference criterion: the queried type is
      // different from the assigned type
      VCExpr! typesEq = VCExpressionGenerator.True;
      foreach (VCExprLetBinding! b in letBindings)
        typesEq = Gen.AndSimp(typesEq, Gen.Eq(b.V, b.E));
      VCExpr! matrix2 = Gen.Or(typesEq, selectEq);
      VCExpr! conjunct2 = Gen.Forall(quantifiedVars, triggers,
                                     "mapAx2:" + select.Name, matrix2);
      axiom = Gen.AndSimp(axiom, conjunct2);

      return axiom;
    }

  }

  /////////////////////////////////////////////////////////////////////////////

  public class TypeEraserPremisses : TypeEraser {

    private readonly TypeAxiomBuilderPremisses! AxBuilderPremisses;

    private OpTypeEraser OpEraserAttr = null;
    protected override OpTypeEraser! OpEraser { get {
      if (OpEraserAttr == null)
        OpEraserAttr = new OpTypeEraserPremisses(this, AxBuilderPremisses, Gen);
      return OpEraserAttr;
    } }

    public TypeEraserPremisses(TypeAxiomBuilderPremisses! axBuilder,
                               VCExpressionGenerator! gen) {
      base(axBuilder, gen);
      this.AxBuilderPremisses = axBuilder;
    }

    ////////////////////////////////////////////////////////////////////////////

    public override VCExpr! Visit(VCExprQuantifier! node,
                                  VariableBindings! oldBindings) {
      VariableBindings bindings = oldBindings.Clone();

      // determine the bound vars that actually occur in the body or
      // in any of the triggers (if some variables do not occur, we
      // need to take special care of type parameters that only occur
      // in the types of such variables)
      FreeVariableCollector coll = new FreeVariableCollector ();
      coll.Collect(node.Body);
      foreach (VCTrigger trigger in node.Triggers) {
        if (trigger.Pos)
          foreach (VCExpr! e in trigger.Exprs)
            coll.Collect(e);
      }

      List<VCExprVar!> occurringVars = new List<VCExprVar!> (node.BoundVars.Count);
      foreach (VCExprVar var in node.BoundVars)
        if (coll.FreeTermVars.ContainsKey(var))
          occurringVars.Add(var);

      occurringVars.TrimExcess();

      // bound term variables are replaced with bound term variables typed in
      // a simpler way
      List<VCExprVar!>! newBoundVars =
        BoundVarsAfterErasure(occurringVars, bindings);
      VCExpr! newNode = HandleQuantifier(node, occurringVars,
                                         newBoundVars, bindings);

      if (!(newNode is VCExprQuantifier) || !IsUniversalQuantifier(node))
        return newNode;

      VariableBindings! bindings2;
      if (!RedoQuantifier(node, (VCExprQuantifier)newNode, occurringVars, oldBindings,
                          out bindings2, out newBoundVars))
        return newNode;

      return HandleQuantifier(node, occurringVars,
                              newBoundVars, bindings2);
    }

    private VCExpr! GenTypePremisses(List<VCExprVar!>! oldBoundVars,
                                     List<VCExprVar!>! newBoundVars,
                                     IDictionary<TypeVariable!, VCExpr!>!
                                                             typeVarTranslation,
                                     List<VCExprLetBinding!>! typeVarBindings,
                                     out List<VCTrigger!>! triggers) {
      // build a substitution of the type variables that it can be checked
      // whether type premisses are trivial
      VCExprSubstitution! typeParamSubstitution = new VCExprSubstitution ();
      foreach (VCExprLetBinding! binding in typeVarBindings)
        typeParamSubstitution[binding.V] = binding.E;
      SubstitutingVCExprVisitor! substituter = new SubstitutingVCExprVisitor (Gen);

      List<VCExpr!>! typePremisses = new List<VCExpr!> (newBoundVars.Count);
      triggers = new List<VCTrigger!> (newBoundVars.Count);

      for (int i = 0; i < newBoundVars.Count; ++i) {
        VCExprVar! oldVar = oldBoundVars[i];
        VCExprVar! newVar = newBoundVars[i];

        VCExpr! typePremiss =
          AxBuilderPremisses.GenVarTypeAxiom(newVar, oldVar.Type,
                                             typeVarTranslation);

        if (!IsTriviallyTrue(substituter.Mutate(typePremiss,
                                                typeParamSubstitution))) {
          typePremisses.Add(typePremiss);
          // generate a negative trigger for the variable occurrence
          // in the type premiss
          triggers.Add(Gen.Trigger(false,
                         HelperFuns.ToList(AxBuilderPremisses.TypeOf(newVar))));
        }
      }

      typePremisses.TrimExcess();
      triggers.TrimExcess();
      
      return Gen.NAry(VCExpressionGenerator.AndOp, typePremisses);
    }

    // these optimisations should maybe be moved into a separate
    // visitor (peep-hole optimisations)
    private bool IsTriviallyTrue(VCExpr! expr) {
      if (expr.Equals(VCExpressionGenerator.True))
        return true;

      if (expr is VCExprNAry) {
        VCExprNAry! naryExpr = (VCExprNAry)expr;
        if (naryExpr.Op.Equals(VCExpressionGenerator.EqOp) &&
            naryExpr[0].Equals(naryExpr[1]))
          return true;
      }

      return false;
    }

    private VCExpr! HandleQuantifier(VCExprQuantifier! node,
                                     List<VCExprVar!>! occurringVars,
                                     List<VCExprVar!>! newBoundVars,
                                     VariableBindings! bindings) {
      List<VCExprLetBinding!>! typeVarBindings =
        AxBuilderPremisses.GenTypeParamBindings(node.TypeParameters, occurringVars, bindings, true);

      // Check whether some of the type parameters could not be
      // determined from the bound variable types. In this case, we
      // quantify explicitly over these variables
      if (typeVarBindings.Count < node.TypeParameters.Count) {
        foreach (TypeVariable! var in node.TypeParameters) {
          if (!exists{VCExprLetBinding! b in typeVarBindings; b.V.Equals(var)})
            newBoundVars.Add((VCExprVar)bindings.TypeVariableBindings[var]);
        }
      }

      // the lists of old and new bound variables for which type
      // antecedents are to be generated
      List<VCExprVar!>! varsWithTypeSpecs = new List<VCExprVar!> ();
      List<VCExprVar!>! newVarsWithTypeSpecs = new List<VCExprVar!> ();
      if (!IsUniversalQuantifier(node) ||
          CommandLineOptions.Clo.TypeEncodingMethod
                == CommandLineOptions.TypeEncoding.Predicates) {
        foreach (VCExprVar! oldVar in occurringVars) {
          varsWithTypeSpecs.Add(oldVar);
          newVarsWithTypeSpecs.Add(bindings.VCExprVarBindings[oldVar]);
        }
      } // else, no type antecedents are created for any variables

      List<VCTrigger!>! furtherTriggers;
      VCExpr! typePremisses =
        GenTypePremisses(varsWithTypeSpecs, newVarsWithTypeSpecs,
                         bindings.TypeVariableBindings,
                         typeVarBindings, out furtherTriggers);

      List<VCTrigger!>! newTriggers = MutateTriggers(node.Triggers, bindings);
      newTriggers.AddRange(furtherTriggers);
      newTriggers = AddLets2Triggers(newTriggers, typeVarBindings);

      VCExpr! newBody = Mutate(node.Body, bindings);

      // assemble the new quantified formula

      if (CommandLineOptions.Clo.TypeEncodingMethod
                == CommandLineOptions.TypeEncoding.None) {
        typePremisses = VCExpressionGenerator.True;
      }                

      VCExpr! bodyWithPremisses =
        AxBuilderPremisses.AddTypePremisses(typeVarBindings, typePremisses,
                                            node.Quan == Quantifier.ALL,
                                            AxBuilder.Cast(newBody, Type.Bool));

      if (newBoundVars.Count == 0)  // might happen that no bound variables are left
        return bodyWithPremisses;

      foreach(VCExprVar! v in newBoundVars) {
        if (v.Type == AxBuilderPremisses.U) {
          newTriggers.Add(Gen.Trigger(false, AxBuilderPremisses.Cast(v, Type.Int)));
          newTriggers.Add(Gen.Trigger(false, AxBuilderPremisses.Cast(v, Type.Bool)));
        }
      }

      return Gen.Quantify(node.Quan, new List<TypeVariable!> (), newBoundVars,
                          newTriggers, node.Infos, bodyWithPremisses);
    }
    
    // check whether we need to add let-binders for any of the type
    // parameters to the triggers (otherwise, the triggers will
    // contain unbound/dangling variables for such parameters)
    private List<VCTrigger!>! AddLets2Triggers(List<VCTrigger!>! triggers,
                                               List<VCExprLetBinding!>! typeVarBindings) {
      List<VCTrigger!>! triggersWithLets = new List<VCTrigger!> (triggers.Count);

      foreach (VCTrigger! t in triggers) {
        List<VCExpr!>! exprsWithLets = new List<VCExpr!> (t.Exprs.Count);

        bool changed = false;
        foreach (VCExpr! e in t.Exprs) {
          Dictionary<VCExprVar!,object>! freeVars =
            FreeVariableCollector.FreeTermVariables(e);

          if (exists{VCExprLetBinding! b in typeVarBindings;
                     freeVars.ContainsKey(b.V)}) {
            exprsWithLets.Add(Gen.Let(typeVarBindings, e));
            changed = true;
          } else {
            exprsWithLets.Add(e);
          }
        }
          
        if (changed)
          triggersWithLets.Add(Gen.Trigger(t.Pos, exprsWithLets));
        else
          triggersWithLets.Add(t);
      }
      
      return triggersWithLets;
    }

  }

  //////////////////////////////////////////////////////////////////////////////

  public class OpTypeEraserPremisses : OpTypeEraser {

    private TypeAxiomBuilderPremisses! AxBuilderPremisses;

    public OpTypeEraserPremisses(TypeEraserPremisses! eraser,
                                 TypeAxiomBuilderPremisses! axBuilder,
                                 VCExpressionGenerator! gen) {
      base(eraser, axBuilder, gen);
      this.AxBuilderPremisses = axBuilder;
    }

    private VCExpr! HandleFunctionOp(Function! newFun,
                                     List<Type!>! typeArgs,
                                     IEnumerable<VCExpr!>! oldArgs,
                                     VariableBindings! bindings) {
      // UGLY: the code for tracking polarities should be factored out
      int oldPolarity = Eraser.Polarity;
      Eraser.Polarity = 0;

      List<VCExpr!>! newArgs = new List<VCExpr!> (typeArgs.Count);

      // translate the explicit type arguments
      foreach (Type! t in typeArgs)
        newArgs.Add(AxBuilder.Type2Term(t, bindings.TypeVariableBindings));

      // recursively translate the value arguments
      foreach (VCExpr! arg in oldArgs) {
        Type! newType = ((!)newFun.InParams[newArgs.Count]).TypedIdent.Type;
        newArgs.Add(AxBuilder.Cast(Eraser.Mutate(arg, bindings), newType));
      }

      Eraser.Polarity = oldPolarity;
      return Gen.Function(newFun, newArgs);
    }

    public override VCExpr! VisitSelectOp   (VCExprNAry! node,
                                             VariableBindings! bindings) {
      MapType! mapType = node[0].Type.AsMap;
      TypeSeq! instantiations; // not used
      Function! select =
        AxBuilder.MapTypeAbstracter.Select(mapType, out instantiations);

      List<int>! explicitTypeParams =
        AxBuilderPremisses.MapTypeAbstracterPremisses
                          .ExplicitSelectTypeParams(mapType);
      assert select.InParams.Length == explicitTypeParams.Count + node.Arity;

      List<Type!>! typeArgs = new List<Type!> (explicitTypeParams.Count);
      foreach (int i in explicitTypeParams)
        typeArgs.Add(node.TypeArguments[i]);
      return HandleFunctionOp(select, typeArgs, node, bindings);
    }

    public override VCExpr! VisitStoreOp    (VCExprNAry! node,
                                             VariableBindings! bindings) {
      TypeSeq! instantiations; // not used
      Function! store =
        AxBuilder.MapTypeAbstracter.Store(node[0].Type.AsMap, out instantiations);
      return HandleFunctionOp(store,
                              // the store function never has explicit
                              // type parameters
                              new List<Type!> (),
                              node, bindings);
    }

    public override VCExpr! VisitBoogieFunctionOp (VCExprNAry! node,
                                                   VariableBindings! bindings) {
      Function! oriFun = ((VCExprBoogieFunctionOp)node.Op).Func;
      UntypedFunction untypedFun = AxBuilderPremisses.Typed2Untyped(oriFun);
      assert untypedFun.Fun.InParams.Length ==
             untypedFun.ExplicitTypeParams.Count + node.Arity;

      List<Type!>! typeArgs =
        ExtractTypeArgs(node,
                        oriFun.TypeParameters, untypedFun.ExplicitTypeParams);
      return HandleFunctionOp(untypedFun.Fun, typeArgs, node, bindings);
    }

    private List<Type!>! ExtractTypeArgs(VCExprNAry! node,
                                         TypeVariableSeq! allTypeParams,
                                         List<TypeVariable!>! explicitTypeParams) {
      List<Type!>! res = new List<Type!> (explicitTypeParams.Count);
      foreach (TypeVariable! var in explicitTypeParams)
        // this lookup could be optimised
        res.Add(node.TypeArguments[allTypeParams.IndexOf(var)]);
      return res;
    }
  }


}