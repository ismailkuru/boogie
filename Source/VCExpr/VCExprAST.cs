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

// Prover-independent syntax trees for representing verification conditions
// The language can be seen as a simple polymorphically typed first-order logic,
// very similar to the expression language of Boogie

namespace Microsoft.Boogie
{
  using Microsoft.Boogie.VCExprAST;

  public class VCExpressionGenerator
  {
    public static readonly VCExpr! False = new VCExprLiteral (Type.Bool);
    public static readonly VCExpr! True  = new VCExprLiteral (Type.Bool);

    private Function ControlFlowFunction = null;
    public VCExpr! ControlFlowFunctionApplication(VCExpr! e1, VCExpr! e2)
    {
      if (ControlFlowFunction == null) {
        Formal! first = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", Microsoft.Boogie.Type.Int), true);
        Formal! second = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", Microsoft.Boogie.Type.Int), true);
        VariableSeq! inputs = new VariableSeq();
        inputs.Add(first);
        inputs.Add(second);
        Formal! returnVar = new Formal(Token.NoToken, new TypedIdent(Token.NoToken, "", Microsoft.Boogie.Type.Int), false);
        ControlFlowFunction = new Function(Token.NoToken, "ControlFlow", inputs, returnVar);
      }
      List<VCExpr!> args = new List<VCExpr!>();
      args.Add(e1);
      args.Add(e2);
      return Function(BoogieFunctionOp(ControlFlowFunction), args);
    }
    
    public VCExpr! Integer(BigNum x) {
      return new VCExprIntLit(x);
    }

    public VCExpr! Function(VCExprOp! op,
                            List<VCExpr!>! arguments,
                            List<Type!>! typeArguments) {
      if (typeArguments.Count > 0)
        return new VCExprMultiAry(op, arguments, typeArguments);

      switch (arguments.Count) {
      case 0: return new VCExprNullary(op);
      case 1: return new VCExprUnary(op, arguments);
      case 2: return new VCExprBinary(op, arguments);
      default: return new VCExprMultiAry(op, arguments);
      }
    }

    public VCExpr! Function(VCExprOp! op, List<VCExpr!>! arguments) {
      return Function(op, arguments, VCExprNAry.EMPTY_TYPE_LIST);
    }

    public VCExpr! Function(VCExprOp! op, params VCExpr[]! arguments)
      requires forall{int i in (0:arguments.Length); arguments[i] != null};
    {
      return Function(op,
                      HelperFuns.ToNonNullList(arguments),
                      VCExprNAry.EMPTY_TYPE_LIST);
    }

    public VCExpr! Function(VCExprOp! op, VCExpr[]! arguments, Type[]! typeArguments)
      requires forall{int i in (0:arguments.Length); arguments[i] != null};
      requires forall{int i in (0:typeArguments.Length); typeArguments[i] != null};
    {
      return Function(op,
                      HelperFuns.ToNonNullList(arguments),
                      HelperFuns.ToNonNullList(typeArguments));
    }

    public VCExpr! Function(Function! op, List<VCExpr!>! arguments) {
      return Function(BoogieFunctionOp(op), arguments, VCExprNAry.EMPTY_TYPE_LIST);
    }

    public VCExpr! Function(Function! op, params VCExpr[]! arguments)
      requires forall{int i in (0:arguments.Length); arguments[i] != null};
    {
      return Function(BoogieFunctionOp(op), arguments);
    }


    // The following method should really be called "ReduceLeft". It must
    // only be used for the binary operators "and" and "or"
    public VCExpr! NAry(VCExprOp! op, List<VCExpr!>! args) {
      return NAry(op, args.ToArray());
    }

    public VCExpr! NAry(VCExprOp! op, params VCExpr[]! args)
      requires forall{int i in (0:args.Length); args[i] != null};
      requires op == AndOp || op == OrOp; {
      bool and = (op == AndOp);

      VCExpr! e = and ? True : False;
      foreach (VCExpr a in args) {
        e = and ? AndSimp(e, (!)a) : OrSimp(e, (!)a);
      }
      return e;
    }      

    ////////////////////////////////////////////////////////////////////////////////

    public static readonly VCExprOp! NotOp     = new VCExprNAryOp (1, Type.Bool);
    public static readonly VCExprOp! EqOp      = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! NeqOp     = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! AndOp     = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! OrOp      = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! ImpliesOp = new VCExprNAryOp (2, Type.Bool);

    public VCExprDistinctOp! DistinctOp(int arity) {
      return new VCExprDistinctOp (arity);
    }

    public VCExpr! Not(List<VCExpr!>! args) 
      requires args.Count == 1;  {
      return Function(NotOp, args);
    }

    public VCExpr! Not(VCExpr! e0) {
      return Function(NotOp, e0);
    }
    public VCExpr! Eq(VCExpr! e0, VCExpr! e1) {
      return Function(EqOp, e0, e1);
    }
    public VCExpr! Neq(VCExpr! e0, VCExpr! e1) {
      return Function(NeqOp, e0, e1);
    }
    public VCExpr! And(VCExpr! e0, VCExpr! e1) {
      return Function(AndOp, e0, e1);
    }
    public VCExpr! Or(VCExpr! e0, VCExpr! e1) {
      return Function(OrOp, e0, e1);
    }
    public VCExpr! Implies(VCExpr! e0, VCExpr! e1) {
      return Function(ImpliesOp, e0, e1);
    }
    public VCExpr! Distinct(List<VCExpr!>! args) {
      if (args.Count <= 1)
        // trivial case
        return True;
      return Function(DistinctOp(args.Count), args);
    }

    ///////////////////////////////////////////////////////////////////////////
    // Versions of the propositional operators that automatically simplify in
    // certain cases (for example, if one of the operators is True or False)

    public VCExpr! NotSimp(VCExpr! e0) {
      if (e0.Equals(True))
        return False;
      if (e0.Equals(False))
        return True;
      return Not(e0);
    }
    public VCExpr! AndSimp(VCExpr! e0, VCExpr! e1) {
      if (e0.Equals(True))
        return e1;
      if (e1.Equals(True))
        return e0;
      if (e0.Equals(False) || e1.Equals(False))
        return False;
      return And(e0, e1);
    }
    public VCExpr! OrSimp(VCExpr! e0, VCExpr! e1) {
      if (e0.Equals(False))
        return e1;
      if (e1.Equals(False))
        return e0;
      if (e0.Equals(True) || e1.Equals(True))
        return True;
      return Or(e0, e1);
    }
    public VCExpr! ImpliesSimp(VCExpr! e0, VCExpr! e1) {
      if (e0.Equals(True))
        return e1;
      if (e1.Equals(False))
        return NotSimp(e0);
      if (e0.Equals(False) || e1.Equals(True))
        return True;
      // attempt to save on the depth of expressions (to reduce chances of stack overflows)
      while (e1 is VCExprBinary) {
        VCExprBinary n = (VCExprBinary)e1;
        if (n.Op == ImpliesOp) {
          if (AndSize(n[0]) <= AndSize(e0)) {
            // combine the antecedents
            e0 = And(e0, n[0]);
            e1 = n[1];
            continue;
          }
        }
        break;
      }
      return Implies(e0, e1);
    }
    
    ///<summary>
    /// Returns some measure of the number of conjuncts in e.  This could be the total number of conjuncts in all
    /// top-most layers of the expression, or it can simply be the length of the left-prong of this and-tree.  The
    /// important thing is that: AndSize(e0) >= AndSize(31) ==> AndSize(And(e0,e1)) > AndSize(e0).
    ///</summary>
    int AndSize(VCExpr! e) {
      int n = 1;
      while (true) {
        VCExprNAry nary = e as VCExprNAry;
        if (nary != null && nary.Op == AndOp && 2 <= nary.Arity) {
          e = nary[0];
          n++;
        } else {
          return n;
        }
      }
    }

    ////////////////////////////////////////////////////////////////////////////////
    // Further operators

    public static readonly VCExprOp! AddOp            = new VCExprNAryOp (2, Type.Int);
    public static readonly VCExprOp! SubOp            = new VCExprNAryOp (2, Type.Int);
    public static readonly VCExprOp! MulOp            = new VCExprNAryOp (2, Type.Int);
    public static readonly VCExprOp! DivOp            = new VCExprNAryOp (2, Type.Int);
    public static readonly VCExprOp! ModOp            = new VCExprNAryOp (2, Type.Int);
    public static readonly VCExprOp! LtOp             = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! LeOp             = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! GtOp             = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! GeOp             = new VCExprNAryOp (2, Type.Bool);
    public static readonly VCExprOp! SubtypeOp        = new VCExprNAryOp (2, Type.Bool);
    // ternary version of the subtype operator, the first argument of which gives
    // the type of the compared terms
    public static readonly VCExprOp! Subtype3Op       = new VCExprNAryOp (3, Type.Bool);
    public static readonly VCExprOp! IfThenElseOp     = new VCExprIfThenElseOp();
    
    public static readonly VCExprOp! TickleBoolOp     = new VCExprCustomOp("tickleBool", 1, Type.Bool);

    public VCExprOp! BoogieFunctionOp(Function! func) {
      return new VCExprBoogieFunctionOp(func);
    }

    // Bitvector nodes

    public VCExpr! Bitvector(BvConst! bv) {
      return Function(new VCExprBvOp(bv.Bits), Integer(bv.Value));
    }

    public VCExpr! BvExtract(VCExpr! bv, int bits, int start, int end) {
      return Function(new VCExprBvExtractOp(start, end, bits), bv);
    }

    public VCExpr! BvConcat(VCExpr! bv1, VCExpr! bv2) {
      return Function(new VCExprBvConcatOp(bv1.Type.BvBits, bv2.Type.BvBits), bv1, bv2);
    }

    public VCExpr! AtMost(VCExpr! smaller, VCExpr! greater) {
      return Function(SubtypeOp, smaller, greater);
    }


    ////////////////////////////////////////////////////////////////////////////////
    // Dispatcher for the visitor

    // the declared singleton operators
    internal enum SingletonOp { NotOp, EqOp, NeqOp, AndOp, OrOp, ImpliesOp,
                                AddOp, SubOp, MulOp,
                                DivOp, ModOp, LtOp, LeOp, GtOp, GeOp, SubtypeOp,
                                Subtype3Op, BvConcatOp };
    internal static Dictionary<VCExprOp!, SingletonOp>! SingletonOpDict;

    static VCExpressionGenerator() {
      SingletonOpDict = new Dictionary<VCExprOp!, SingletonOp> ();
      SingletonOpDict.Add(NotOp,     SingletonOp.NotOp);
      SingletonOpDict.Add(EqOp,      SingletonOp.EqOp);
      SingletonOpDict.Add(NeqOp,     SingletonOp.NeqOp);
      SingletonOpDict.Add(AndOp,     SingletonOp.AndOp);
      SingletonOpDict.Add(OrOp,      SingletonOp.OrOp);
      SingletonOpDict.Add(ImpliesOp, SingletonOp.ImpliesOp);
      SingletonOpDict.Add(AddOp,     SingletonOp.AddOp);
      SingletonOpDict.Add(SubOp,     SingletonOp.SubOp);
      SingletonOpDict.Add(MulOp,     SingletonOp.MulOp);
      SingletonOpDict.Add(DivOp,     SingletonOp.DivOp);
      SingletonOpDict.Add(ModOp,     SingletonOp.ModOp);
      SingletonOpDict.Add(LtOp,      SingletonOp.LtOp);
      SingletonOpDict.Add(LeOp,      SingletonOp.LeOp);
      SingletonOpDict.Add(GtOp,      SingletonOp.GtOp);
      SingletonOpDict.Add(GeOp,      SingletonOp.GeOp);
      SingletonOpDict.Add(SubtypeOp, SingletonOp.SubtypeOp);
      SingletonOpDict.Add(Subtype3Op,SingletonOp.Subtype3Op);
    }

    ////////////////////////////////////////////////////////////////////////////////


    // Let-bindings

    public VCExprLetBinding! LetBinding(VCExprVar! v, VCExpr! e) {
      return new VCExprLetBinding(v, e);
    }
    
    // A "real" let expression. All let-bindings happen simultaneously, i.e.,
    // at this level the order of the bindings does not matter. It is possible to
    // create expressions like   "let x = y, y = 5 in ...". All bound variables are
    // bound in all bound terms/formulas and can occur there, but the dependencies
    // have to be acyclic
    public VCExpr! Let(List<VCExprLetBinding!>! bindings, VCExpr! body) {
      if (bindings.Count == 0)
        // no empty let-bindings
        return body;
      return new VCExprLet(bindings, body);
    }

    public VCExpr! Let(VCExpr! body, params VCExprLetBinding[]! bindings)
      requires forall{int i in (0:bindings.Length); bindings[i] != null};
    {
      return Let(HelperFuns.ToNonNullList(bindings), body);
    }


    /// <summary>
    /// In contrast to the previous method, the following methods are not a general LET.
    ///  Instead, it
    /// is a boolean "LET b = P in Q", where P and Q are predicates, that is allowed to be
    /// encoded as "(b == P) ==> Q" or even as "(P ==> b) ==> Q"
    /// (or "(P ==> b) and Q" in negative positions).
    /// The method assumes that the variables in the bindings are unique in the entire formula
    /// to be produced, which allows the implementation to ignore scope issues in the event that
    /// it needs to generate an alternate expression for LET.
    /// </summary>


    // Turn let-bindings let v = E in ... into implications E ==> v
    public VCExpr! AsImplications(List<VCExprLetBinding!>! bindings) {
      VCExpr! antecedents = True;
      foreach (VCExprLetBinding b in bindings)
        // turn "LET_binding v = E" into "v <== E"
        antecedents = AndSimp(antecedents, Implies(b.E, b.V));
      return antecedents;
    }

    // Turn let-bindings let v = E in ... into equations v == E
    public VCExpr! AsEquations(List<VCExprLetBinding!>! bindings) {
      VCExpr! antecedents = True;
      foreach (VCExprLetBinding b in bindings)
        // turn "LET_binding v = E" into "v <== E"
        antecedents = AndSimp(antecedents, Eq(b.E, b.V));
      return antecedents;
    }



    // Maps

    public VCExpr! Select(params VCExpr[]! allArgs)
      requires forall{int i in (0:allArgs.Length); allArgs[i] != null};
    {
      return Function(new VCExprSelectOp(allArgs.Length - 1, 0),
                      HelperFuns.ToNonNullList(allArgs),
                      VCExprNAry.EMPTY_TYPE_LIST);
    }

    public VCExpr! Select(VCExpr[]! allArgs, Type[]! typeArgs)
      requires 1 <= allArgs.Length;
      requires forall{int i in (0:allArgs.Length); allArgs[i] != null};
      requires forall{int i in (0:typeArgs.Length); typeArgs[i] != null};
    {
      return Function(new VCExprSelectOp(allArgs.Length - 1, typeArgs.Length),
                      allArgs, typeArgs);
    }

    public VCExpr! Select(List<VCExpr!>! allArgs, List<Type!>! typeArgs)
      requires 1 <= allArgs.Count;
    {
      return Function(new VCExprSelectOp(allArgs.Count - 1, typeArgs.Count),
                      allArgs, typeArgs);
    }

    public VCExpr! Store(params VCExpr[]! allArgs)
      requires forall{int i in (0:allArgs.Length); allArgs[i] != null};
    {
      return Function(new VCExprStoreOp(allArgs.Length - 2, 0),
                      HelperFuns.ToNonNullList(allArgs),
                      VCExprNAry.EMPTY_TYPE_LIST);
    }

    public VCExpr! Store(VCExpr[]! allArgs, Type[]! typeArgs)
      requires 2 <= allArgs.Length;
      requires forall{int i in (0:allArgs.Length); allArgs[i] != null};
      requires forall{int i in (0:typeArgs.Length); typeArgs[i] != null};
    {
      return Function(new VCExprStoreOp(allArgs.Length - 2, typeArgs.Length),
                      allArgs, typeArgs);
    }

    public VCExpr! Store(List<VCExpr!>! allArgs, List<Type!>! typeArgs)
      requires 2 <= allArgs.Count;
    {
      return Function(new VCExprStoreOp(allArgs.Count - 2, typeArgs.Count),
                      allArgs, typeArgs);
    }


    // Labels

    public VCExprLabelOp! LabelOp(bool pos, string! l) {
      return new VCExprLabelOp(pos, l);
    }

    public VCExpr! LabelNeg(string! label, VCExpr! e) {
      if (e.Equals(True)) {
        return e;  // don't bother putting negative labels around True (which will expose the True to further peephole optimizations)
      }
      return Function(LabelOp(false, label), e);
    }
    public VCExpr! LabelPos(string! label, VCExpr! e) {
      return Function(LabelOp(true, label), e);
    }

    // Quantifiers

    public VCExpr! Quantify(Quantifier quan,
                            List<TypeVariable!>! typeParams, List<VCExprVar!>! vars,
                            List<VCTrigger!>! triggers, VCQuantifierInfos! infos,
                            VCExpr! body) {
      return new VCExprQuantifier(quan, typeParams, vars, triggers, infos, body);
    }

    public VCExpr! Forall(List<TypeVariable!>! typeParams, List<VCExprVar!>! vars,
                          List<VCTrigger!>! triggers, VCQuantifierInfos! infos,
                          VCExpr! body) {
      return Quantify(Quantifier.ALL, typeParams, vars, triggers, infos, body);
    }
    public VCExpr! Forall(List<VCExprVar!>! vars,
                          List<VCTrigger!>! triggers,
                          string! qid, VCExpr! body) {
      return Quantify(Quantifier.ALL, new List<TypeVariable!> (), vars,
                      triggers, new VCQuantifierInfos (qid, -1, false, null), body);
    }
    public VCExpr! Forall(List<VCExprVar!>! vars,
                          List<VCTrigger!>! triggers,
                          VCExpr! body) {
      return Quantify(Quantifier.ALL, new List<TypeVariable!> (), vars,
                      triggers, new VCQuantifierInfos (null, -1, false, null), body);
    }
    public VCExpr! Forall(VCExprVar! var, VCTrigger! trigger, VCExpr! body) {
      return Forall(HelperFuns.ToNonNullList(var), HelperFuns.ToNonNullList(trigger), body);
    }
    public VCExpr! Exists(List<TypeVariable!>! typeParams, List<VCExprVar!>! vars,
                          List<VCTrigger!>! triggers, VCQuantifierInfos! infos,
                          VCExpr! body) {
      return Quantify(Quantifier.EX, typeParams, vars, triggers, infos, body);
    }
    public VCExpr! Exists(List<VCExprVar!>! vars,
                          List<VCTrigger!>! triggers,
                          VCExpr! body) {
      return Quantify(Quantifier.EX, new List<TypeVariable!> (), vars,
                      triggers, new VCQuantifierInfos (null, -1, false, null), body);
    }
    public VCExpr! Exists(VCExprVar! var, VCTrigger! trigger, VCExpr! body) {
      return Exists(HelperFuns.ToNonNullList(var), HelperFuns.ToNonNullList(trigger), body);
    }

    public VCTrigger! Trigger(bool pos, List<VCExpr!>! exprs) {
      return new VCTrigger(pos, exprs);
    }

    public VCTrigger! Trigger(bool pos, params VCExpr[]! exprs)
      requires forall{int i in (0:exprs.Length); exprs[i] != null};
    {
      return Trigger(pos, HelperFuns.ToNonNullList(exprs));
    }

    // Reference to a bound or free variable

    public VCExprVar! Variable(string! name, Type! type) {
      return new VCExprVar(name, type);
    }
  }
}

namespace Microsoft.Boogie.VCExprAST
{

  public class HelperFuns {
    public static bool SameElements(IEnumerable! a, IEnumerable! b) {
      IEnumerator ia = a.GetEnumerator();
      IEnumerator ib = b.GetEnumerator();
      while (true) {
        if (ia.MoveNext()) {
          if (ib.MoveNext()) {
            if (!((!)ia.Current).Equals(ib.Current))
              return false;
          } else {
              return false;
          }
        } else {
          return !ib.MoveNext();
        }
      }
      return true;
    }

    public static int PolyHash(int init, int factor, IEnumerable! a) {
      int res = init;
      foreach(object x in a)
        res = res * factor + ((!)x).GetHashCode();
      return res;
    }

    public static List<T>! ToList<T>(IEnumerable<T>! l) {
      List<T>! res = new List<T> ();
      foreach (T x in l)
        res.Add(x);
      return res;
    }

    public static TypeSeq! ToTypeSeq(VCExpr[]! exprs, int startIndex)
      requires forall{int i in (0:exprs.Length); exprs[i] != null};
    {
      TypeSeq! res = new TypeSeq ();
      for (int i = startIndex; i < exprs.Length; ++i)
        res.Add(((!)exprs[i]).Type);
      return res;
    }

    public static List<T!>! ToNonNullList<T> (params T[]! args) {
      List<T!>! res = new List<T!> (args.Length);
      foreach (T t in args)
        res.Add((!)t);
      return res;
    }

    public static IDictionary<A, B>! Clone<A,B>(IDictionary<A,B>! dict) {
      IDictionary<A,B>! res = new Dictionary<A,B> (dict.Count);
      foreach (KeyValuePair<A,B> pair in dict)
        res.Add(pair);
      return res;
    }
  }

  public abstract class VCExpr {
    public abstract Type! Type { get; }

    public abstract Result Accept<Result, Arg>(IVCExprVisitor<Result, Arg>! visitor, Arg arg);

    [Pure]
    public override string! ToString() {
      StringWriter! sw = new StringWriter();
      VCExprPrinter! printer = new VCExprPrinter ();
      printer.Print(this, sw);
      return (!)sw.ToString();
    }
  }

  /////////////////////////////////////////////////////////////////////////////////
  // Literal expressions

  public class VCExprLiteral : VCExpr {
    private readonly Type! LitType;
    public override Type! Type { get { return LitType; } }
    internal VCExprLiteral(Type! type) {
      this.LitType = type;
    }
    public override Result Accept<Result, Arg>(IVCExprVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.Visit(this, arg);
    }
  }

  public class VCExprIntLit : VCExprLiteral
  {
    public readonly BigNum Val;
    internal VCExprIntLit(BigNum val) {
      base(Type.Int);
      this.Val = val;
    }    
    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprIntLit)
        return Val == ((VCExprIntLit)that).Val;
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Val.GetHashCode() * 72321;
    }
  }

  /////////////////////////////////////////////////////////////////////////////////
  // Operator expressions with fixed arity

  public abstract class VCExprNAry : VCExpr, IEnumerable<VCExpr!> {
    public readonly VCExprOp! Op;
    public int Arity { get { return Op.Arity; } }
    public int TypeParamArity { get { return Op.TypeParamArity; } }
	public int Length { get { return Arity; } }
    // the sub-expressions of the expression
    public abstract VCExpr! this[int index] { get; }

    // the type arguments
    public abstract List<Type!>! TypeArguments { get; }

    [Pure] [GlobalAccess(false)] [Escapes(true,false)]
    public IEnumerator<VCExpr!>! GetEnumerator() {
      for (int i = 0; i < Arity; ++i)
        yield return this[i];
    }
    [Pure] [GlobalAccess(false)] [Escapes(true,false)]
    IEnumerator! System.Collections.IEnumerable.GetEnumerator() {
      for (int i = 0; i < Arity; ++i)
        yield return this[i];
    }

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprNAry) {
        // we compare the subterms iteratively (not recursively)
        // to avoid stack overflows

        VCExprNAryEnumerator enum0 = new VCExprNAryEnumerator(this);
        VCExprNAryEnumerator enum1 = new VCExprNAryEnumerator((VCExprNAry)that);

        while (true) {
          bool next0 = enum0.MoveNext();
          bool next1 = enum1.MoveNext();
          if (next0 != next1)
            return false;
          if (!next0)
            return true;

          VCExprNAry nextExprNAry0 = enum0.Current as VCExprNAry;
          VCExprNAry nextExprNAry1 = enum1.Current as VCExprNAry;

          if ((nextExprNAry0 == null) != (nextExprNAry1 == null))
            return false;
          if (nextExprNAry0 != null && nextExprNAry1 != null) {
            if (!nextExprNAry0.Op.Equals(nextExprNAry1.Op))
              return false;
          } else {
            if (!((!)enum0.Current).Equals(enum1.Current))
              return false;
          }
        }
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return HelperFuns.PolyHash(Op.GetHashCode() * 123 + Arity * 61521,
                                 3, this);
    }

    internal VCExprNAry(VCExprOp! op) {
      this.Op = op;
    }
    public override Result Accept<Result, Arg>(IVCExprVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.Visit(this, arg);
    }
    public Result Accept<Result, Arg>(IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return Op.Accept(this, visitor, arg);
    }

    internal static readonly List<Type!>! EMPTY_TYPE_LIST = new List<Type!> ();
    internal static readonly List<VCExpr!>! EMPTY_VCEXPR_LIST = new List<VCExpr!> ();
  }

  // We give specialised implementations for nullary, unary and binary expressions

  internal class VCExprNullary : VCExprNAry {
    private readonly Type! ExprType;
    public override Type! Type { get { return ExprType; } }
    public override VCExpr! this[int index] { get {
      assert false; // no arguments
    } }

    // the type arguments
    public override List<Type!>! TypeArguments { get {
      return EMPTY_TYPE_LIST;
    } }

    internal VCExprNullary(VCExprOp! op)
      requires op.Arity == 0 && op.TypeParamArity == 0; {
      base(op);
      this.ExprType = op.InferType(EMPTY_VCEXPR_LIST, EMPTY_TYPE_LIST);
    }
  }

  internal class VCExprUnary : VCExprNAry {
    private readonly VCExpr! Argument;
    private readonly Type! ExprType;
    public override Type! Type { get { return ExprType; } }
    public override VCExpr! this[int index] { get {
      assume index == 0;
      return Argument;
    } }

    // the type arguments
    public override List<Type!>! TypeArguments { get {
      return EMPTY_TYPE_LIST;
    } }

    internal VCExprUnary(VCExprOp! op, List<VCExpr!>! arguments)
      requires op.Arity == 1 && op.TypeParamArity == 0 && arguments.Count == 1; {
      base(op);
      this.Argument = arguments[0];
      this.ExprType =
        op.InferType(arguments, EMPTY_TYPE_LIST);
    }
    
    internal VCExprUnary(VCExprOp! op, VCExpr! argument)
      requires op.Arity == 1 && op.TypeParamArity == 0; {
      base(op);
      this.Argument = argument;
      // PR: could be optimised so that the argument does
      // not have to be boxed in an array each time
      this.ExprType =
        op.InferType(HelperFuns.ToNonNullList(argument), EMPTY_TYPE_LIST);
    }
  }

  internal class VCExprBinary : VCExprNAry {
    private readonly VCExpr! Argument0;
    private readonly VCExpr! Argument1;
    private readonly Type! ExprType;
    public override Type! Type { get { return ExprType; } }
    public override VCExpr! this[int index] { get {
      switch (index) {
      case 0: return Argument0;
      case 1: return Argument1;
      default: assert false;
      }
    } }

    // the type arguments
    public override List<Type!>! TypeArguments { get {
      return EMPTY_TYPE_LIST;
    } }

    internal VCExprBinary(VCExprOp! op, List<VCExpr!>! arguments)
      requires op.Arity == 2 && op.TypeParamArity == 0 && arguments.Count == 2; {
      base(op);
      this.Argument0 = arguments[0];
      this.Argument1 = arguments[1];
      this.ExprType = op.InferType(arguments, EMPTY_TYPE_LIST);
    }

    internal VCExprBinary(VCExprOp! op, VCExpr! argument0, VCExpr! argument1)
      requires op.Arity == 2 && op.TypeParamArity == 0; {
      base(op);
      this.Argument0 = argument0;
      this.Argument1 = argument1;
      // PR: could be optimised so that the arguments do
      // not have to be boxed in an array each time
      this.ExprType =
        op.InferType(HelperFuns.ToNonNullList(argument0, argument1),
                     EMPTY_TYPE_LIST);
    }
  }

  internal class VCExprMultiAry : VCExprNAry {
    private readonly List<VCExpr!>! Arguments;
    private readonly List<Type!>! TypeArgumentsAttr;

    private readonly Type! ExprType;
    public override Type! Type { get { return ExprType; } }
    public override VCExpr! this[int index] { get {
      assume index >= 0 && index < Arity;
      return (!)Arguments[index];
    } }

    // the type arguments
    public override List<Type!>! TypeArguments { get {
      return TypeArgumentsAttr;
    } }

    internal VCExprMultiAry(VCExprOp! op, List<VCExpr!>! arguments) {
      this(op, arguments, EMPTY_TYPE_LIST);
    }
    internal VCExprMultiAry(VCExprOp! op, List<VCExpr!>! arguments, List<Type!>! typeArguments)
      requires (arguments.Count > 2 || typeArguments.Count > 0);
      requires op.Arity == arguments.Count;
      requires op.TypeParamArity == typeArguments.Count;
    {
      base(op);
      this.Arguments = arguments;
      this.TypeArgumentsAttr = typeArguments;
      this.ExprType = op.InferType(arguments, typeArguments);
    }
  }

  /////////////////////////////////////////////////////////////////////////////////
  // The various operators available

  public abstract class VCExprOp {
    // the number of value parameters
    public abstract int Arity { get; }
    // the number of type parameters
    public abstract int TypeParamArity { get; }

    public abstract Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs);

    public virtual Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      VCExpressionGenerator.SingletonOp op;
      if (VCExpressionGenerator.SingletonOpDict.TryGetValue(this, out op)) {
        switch(op) {
        case VCExpressionGenerator.SingletonOp.NotOp:
          return visitor.VisitNotOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.EqOp:
          return visitor.VisitEqOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.NeqOp:
          return visitor.VisitNeqOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.AndOp:
          return visitor.VisitAndOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.OrOp:
          return visitor.VisitOrOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.ImpliesOp:
          return visitor.VisitImpliesOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.AddOp:
          return visitor.VisitAddOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.SubOp:
          return visitor.VisitSubOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.MulOp:
          return visitor.VisitMulOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.DivOp:
          return visitor.VisitDivOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.ModOp:
          return visitor.VisitModOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.LtOp:
          return visitor.VisitLtOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.LeOp:
          return visitor.VisitLeOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.GtOp:
          return visitor.VisitGtOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.GeOp:
          return visitor.VisitGeOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.SubtypeOp:
          return visitor.VisitSubtypeOp(expr, arg);
        case VCExpressionGenerator.SingletonOp.Subtype3Op:
          return visitor.VisitSubtype3Op(expr, arg);
        case VCExpressionGenerator.SingletonOp.BvConcatOp:
          return visitor.VisitBvConcatOp(expr, arg);
        default:
          assert false;
        }
      } else {
        assert false;
      }
    }
  }

  public class VCExprNAryOp : VCExprOp {
    private readonly Type! OpType;
    private readonly int OpArity;

    public override int Arity { get { return OpArity; } }
    public override int TypeParamArity { get { return 0; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      return OpType;
    }

    internal VCExprNAryOp(int arity, Type! type) {
      this.OpArity = arity;
      this.OpType = type;
    }
  }

  public class VCExprDistinctOp : VCExprNAryOp {
    internal VCExprDistinctOp(int arity) {
      base(arity, Type.Bool);
    }
    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprDistinctOp)
        return Arity == ((VCExprDistinctOp)that).Arity;
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Arity * 917632481;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitDistinctOp(expr, arg);
    }
  }

  public class VCExprLabelOp : VCExprOp {
    public override int Arity { get { return 1; } }
    public override int TypeParamArity { get { return 0; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      return args[0].Type;
    }
    
    public readonly bool pos;
    public readonly string! label;

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprLabelOp) {
        VCExprLabelOp! thatOp = (VCExprLabelOp)that;
        return this.pos == thatOp.pos && this.label.Equals(thatOp.label);
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return (pos ? 9817231 : 7198639) + label.GetHashCode();
    }

    internal VCExprLabelOp(bool pos, string! l) {
      this.pos = pos;
      this.label = pos ? "+" + l : "@" + l;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitLabelOp(expr, arg);
    }
  }

  public class VCExprSelectOp : VCExprOp {
    private readonly int MapArity;
    private readonly int MapTypeParamArity;
    public override int Arity { get { return MapArity + 1; } }
    public override int TypeParamArity { get { return MapTypeParamArity; } }

    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      MapType! mapType = args[0].Type.AsMap;
      assert TypeParamArity == mapType.TypeParameters.Length;
      IDictionary<TypeVariable!, Type!>! subst = new Dictionary<TypeVariable!, Type!> ();
      for (int i = 0; i < TypeParamArity; ++i)
        subst.Add(mapType.TypeParameters[i], typeArgs[i]);
      return mapType.Result.Substitute(subst);
    }
    
    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprSelectOp)
        return Arity == ((VCExprSelectOp)that).Arity &&
          TypeParamArity == ((VCExprSelectOp)that).TypeParamArity;
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Arity * 1212481 + TypeParamArity * 298741;
    }

    internal VCExprSelectOp(int arity, int typeParamArity)
      requires 0 <= arity && 0 <= typeParamArity;
    {
      this.MapArity = arity;
      this.MapTypeParamArity = typeParamArity;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitSelectOp(expr, arg);
    }
  }

  public class VCExprStoreOp : VCExprOp {
    private readonly int MapArity;
    private readonly int MapTypeParamArity;
    public override int Arity { get { return MapArity + 2; } }
    // stores never need explicit type parameters, because also the
    // rhs is a value argument
    public override int TypeParamArity { get { return MapTypeParamArity; } }

    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      return args[0].Type;
    }
    
    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprStoreOp)
        return Arity == ((VCExprStoreOp)that).Arity;
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Arity * 91361821;
    }

    internal VCExprStoreOp(int arity, int typeParamArity)
      requires 0 <= arity && 0 <= typeParamArity;
    {
      this.MapArity = arity;
      this.MapTypeParamArity = typeParamArity;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitStoreOp(expr, arg);
    }
  }

  public class VCExprIfThenElseOp : VCExprOp {
    public override int Arity { get { return 3; } }
    public override int TypeParamArity { get { return 0; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      return args[1].Type;
    }

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprIfThenElseOp)
        return true;
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return 1;
    }

    internal VCExprIfThenElseOp() {
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitIfThenElseOp(expr, arg);
    }
  }
  
  public class VCExprCustomOp : VCExprOp {
    public readonly string! Name;
    int arity;
    public readonly Type! Type;
    public VCExprCustomOp(string! name, int arity, Type! type) {
      this.Name = name;
      this.arity = arity;
      this.Type = type;
    }
    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      VCExprCustomOp t = that as VCExprCustomOp;
      if (t == null)
        return false;
      return this.Name == t.Name && this.arity == t.arity && this.Type == t.Type;
    }
    public override int Arity { get { return arity; } }
    public override int TypeParamArity { get { return 0; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) { return Type; }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitCustomOp(expr, arg);
    }
  }

  /////////////////////////////////////////////////////////////////////////////////
  // Bitvector operators

  public class VCExprBvOp : VCExprOp {
    public readonly int Bits;

    public override int Arity { get { return 1; } }
    public override int TypeParamArity { get { return 0; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      return Type.GetBvType(Bits);
    }

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprBvOp)
        return this.Bits == ((VCExprBvOp)that).Bits;
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Bits * 81748912;
    }

    internal VCExprBvOp(int bits) {
      this.Bits = bits;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitBvOp(expr, arg);
    }
  }

  public class VCExprBvExtractOp : VCExprOp {
    public readonly int Start;
    public readonly int End;
    public readonly int Total;  // the number of bits from which the End-Start bits are extracted

    public override int Arity { get { return 1; } }
    public override int TypeParamArity { get { return 0; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      return Type.GetBvType(End - Start);
    }

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprBvExtractOp) {
        VCExprBvExtractOp! thatExtract = (VCExprBvExtractOp)that;
        return this.Start == thatExtract.Start && this.End == thatExtract.End && this.Total == thatExtract.Total;
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Start * 81912 + End * 978132 + Total * 571289;
    }

    internal VCExprBvExtractOp(int start, int end, int total)
      requires 0 <= start && start <= end && end <= total;
    {
      this.Start = start;
      this.End = end;
      this.Total = total;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitBvExtractOp(expr, arg);
    }
  }

  public class VCExprBvConcatOp : VCExprOp {
    public readonly int LeftSize;
    public readonly int RightSize;
    
    public override int Arity { get { return 2; } }
    public override int TypeParamArity { get { return 0; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      return Type.GetBvType(args[0].Type.BvBits + args[1].Type.BvBits);
    }

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprBvConcatOp) {
        VCExprBvConcatOp thatConcat = (VCExprBvConcatOp)that;
        return this.LeftSize == thatConcat.LeftSize && this.RightSize == thatConcat.RightSize;
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return LeftSize * 81912 + RightSize * 978132;
    }

    internal VCExprBvConcatOp(int leftSize, int rightSize)
      requires 0 <= leftSize && 0 <= rightSize;
    {
      this.LeftSize = leftSize;
      this.RightSize = rightSize;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitBvConcatOp(expr, arg);
    }
  }

  /////////////////////////////////////////////////////////////////////////////////
  // References to user-defined Boogie functions

  public class VCExprBoogieFunctionOp : VCExprOp {
    public readonly Function! Func;

    public override int Arity { get { return Func.InParams.Length; } }
    public override int TypeParamArity { get { return Func.TypeParameters.Length; } }
    public override Type! InferType(List<VCExpr!>! args, List<Type!>! typeArgs) {
      assert TypeParamArity == Func.TypeParameters.Length;
      if (TypeParamArity == 0)
        return ((!)Func.OutParams[0]).TypedIdent.Type;
      IDictionary<TypeVariable!, Type!>! subst = new Dictionary<TypeVariable!, Type!> (TypeParamArity);
      for (int i = 0; i < TypeParamArity; ++i)
        subst.Add(Func.TypeParameters[i], typeArgs[i]);
      return ((!)Func.OutParams[0]).TypedIdent.Type.Substitute(subst);
    }

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprBoogieFunctionOp)
        return this.Func.Equals(((VCExprBoogieFunctionOp)that).Func);
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Func.GetHashCode() + 18731;
    }

    // we require that the result type of the expression is specified, because we
    // do not want to perform full type inference at this point
    internal VCExprBoogieFunctionOp(Function! func) {
      this.Func = func;
    }
    public override Result Accept<Result, Arg>
           (VCExprNAry! expr, IVCExprOpVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.VisitBoogieFunctionOp(expr, arg);
    }
  }

  /////////////////////////////////////////////////////////////////////////////////
  // Binders (quantifiers and let-expressions). We introduce our own class for
  // term variables, but use the Boogie-AST class for type variables

  public class VCExprVar : VCExpr {
    // the name of the variable. Note that the name is not used for comparison,
    // i.e., there can be two distinct variables with the same name
    public readonly string! Name;
    private readonly Type! VarType;
    public override Type! Type { get { return VarType; } }

    internal VCExprVar(string! name, Type! type) {
      this.Name = name;
      this.VarType = type;
    }
    public override Result Accept<Result, Arg>(IVCExprVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.Visit(this, arg);
    }
  }

  public abstract class VCExprBinder : VCExpr {
    public readonly VCExpr! Body;
    public readonly List<TypeVariable!>! TypeParameters;
    public readonly List<VCExprVar!>! BoundVars;
 
    public override Type! Type { get { return Body.Type; } }

    internal VCExprBinder(List<TypeVariable!>! typeParams,
                          List<VCExprVar!>! boundVars,
                          VCExpr! body)
      requires boundVars.Count + typeParams.Count > 0; {     // only nontrivial binders ...
      this.TypeParameters = typeParams;
      this.BoundVars = boundVars;
      this.Body = body;
    }
  }

  public class VCTrigger {
    public readonly bool Pos;
    public readonly List<VCExpr!>! Exprs;

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCTrigger) {
        VCTrigger! thatTrigger = (VCTrigger)that;
        return this.Pos == thatTrigger.Pos &&
          HelperFuns.SameElements(this.Exprs, thatTrigger.Exprs);
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return (Pos ? 913821 : 871334) +
             HelperFuns.PolyHash(123, 7, this.Exprs);
    }

    public VCTrigger(bool pos, List<VCExpr!>! exprs) {
      this.Pos = pos;
      this.Exprs = exprs;
    }
  }

  public class VCQuantifierInfos {
    public readonly string qid;
    public readonly int uniqueId;
    public readonly bool bvZ3Native;
    public QKeyValue attributes;

    public VCQuantifierInfos(string qid, int uniqueId, bool bvZ3Native, QKeyValue attributes) {
      this.qid = qid;
      this.uniqueId = uniqueId;
      this.bvZ3Native = bvZ3Native;
      this.attributes = attributes;
    }
  }

  public enum Quantifier { ALL, EX };

  public class VCExprQuantifier : VCExprBinder {
    public readonly Quantifier Quan;

    public readonly List<VCTrigger!>! Triggers;
    public readonly VCQuantifierInfos! Infos;

    // Equality is /not/ modulo bound renaming at this point
    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprQuantifier) {
        VCExprQuantifier! thatQuan = (VCExprQuantifier)that;
        return this.Quan == thatQuan.Quan &&
               HelperFuns.SameElements(this.Triggers, thatQuan.Triggers) &&
               HelperFuns.SameElements(this.TypeParameters, thatQuan.TypeParameters) &&
               HelperFuns.SameElements(this.BoundVars, thatQuan.BoundVars) &&
               this.Body.Equals(thatQuan.Body);
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return Quan.GetHashCode() +
        HelperFuns.PolyHash(973219, 7, TypeParameters) +
        HelperFuns.PolyHash(998431, 9, BoundVars) +
        HelperFuns.PolyHash(123, 11, Triggers);
    }

    internal VCExprQuantifier(Quantifier kind,
                              List<TypeVariable!>! typeParams,
                              List<VCExprVar!>! boundVars,
                              List<VCTrigger!>! triggers,
                              VCQuantifierInfos! infos,
                              VCExpr! body) {
      base(typeParams, boundVars, body);
      this.Quan = kind;
      this.Triggers = triggers;
      this.Infos = infos;
    }
    public override Result Accept<Result, Arg>(IVCExprVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.Visit(this, arg);
    }
  }

  /////////////////////////////////////////////////////////////////////////////////
  // Let-Bindings

  public class VCExprLetBinding {
    public readonly VCExprVar! V;
    public readonly VCExpr! E;

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprLetBinding) {
        VCExprLetBinding! thatB = (VCExprLetBinding)that;
        return this.V.Equals(thatB.V) && this.E.Equals(thatB.E);
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return V.GetHashCode() * 71261 + E.GetHashCode();
    }
    
    internal VCExprLetBinding(VCExprVar! v, VCExpr! e) {
      this.V = v;
      this.E = e;
      assert v.Type.Equals(e.Type);
    }
  }

  public class VCExprLet : VCExprBinder, IEnumerable<VCExprLetBinding!> {
    private readonly List<VCExprLetBinding!>! Bindings;

    public int Length { get { return Bindings.Count; } }
    public VCExprLetBinding! this[int index] { get {
      return Bindings[index];
    } }

    [Pure][Reads(ReadsAttribute.Reads.Nothing)]
    public override bool Equals(object that) {
      if (Object.ReferenceEquals(this, that))
        return true;
      if (that is VCExprLet) {
        VCExprLet! thatLet = (VCExprLet)that;
        return this.Body.Equals(thatLet.Body) &&
               HelperFuns.SameElements(this, (VCExprLet)that);
      }
      return false;
    }
    [Pure]
    public override int GetHashCode() {
      return HelperFuns.PolyHash(Body.GetHashCode(), 9, Bindings);
    }

    [Pure] [GlobalAccess(false)] [Escapes(true,false)]
    public IEnumerator<VCExprLetBinding!>! GetEnumerator() {
      return Bindings.GetEnumerator();
    }
    [Pure] [GlobalAccess(false)] [Escapes(true,false)]
    IEnumerator! System.Collections.IEnumerable.GetEnumerator() {
      return Bindings.GetEnumerator();
    }

    private static List<VCExprVar!>! toSeq(List<VCExprLetBinding!>! bindings) {
      List<VCExprVar!>! res = new List<VCExprVar!> ();
      foreach (VCExprLetBinding! b in bindings)
        res.Add(b.V);
      return res;
    }

    internal VCExprLet(List<VCExprLetBinding!>! bindings,
                       VCExpr! body) {
      base(new List<TypeVariable!> (), toSeq(bindings), body);
      this.Bindings = bindings;
    }
    public override Result Accept<Result, Arg>(IVCExprVisitor<Result, Arg>! visitor, Arg arg) {
      return visitor.Visit(this, arg);
    }
  }
}