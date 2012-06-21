using System;
using Microsoft.Boogie;
using System.Collections.Generic;
using System.Linq;

namespace GPUVerify {

class VariableDefinitionAnalysis {
  GPUVerifier verifier;
  Implementation impl;

  Dictionary<Variable, Expr> defMap = new Dictionary<Variable, Expr>();
  Dictionary<string, Expr> namedDefMap = new Dictionary<string, Expr>();
  bool changed;

  VariableDefinitionAnalysis(GPUVerifier v, Implementation i) {
    verifier = v;
    impl = i;
  }

  private class IsConstantVisitor : StandardVisitor {
    private VariableDefinitionAnalysis analysis;
    public bool isConstant = true;

    public IsConstantVisitor(VariableDefinitionAnalysis a) {
      analysis = a;
    }

    public override Expr VisitNAryExpr(NAryExpr expr) {
      if (expr.Fun is MapSelect) {
        isConstant = false;
        return expr;
      } else
        return base.VisitNAryExpr(expr);
    }

    public override Expr VisitIdentifierExpr(IdentifierExpr expr) {
      if (expr.Decl is Constant)
        return expr;
      if (!analysis.defMap.ContainsKey(expr.Decl) || analysis.defMap[expr.Decl] == null)
        isConstant = false;
      return expr;
    }
  };

  bool IsConstant(Expr e) {
    var v = new IsConstantVisitor(this);
    v.Visit(e);
    return v.isConstant;
  }

  void UpdateDefMap(Variable v, Expr def) {
    if (!defMap.ContainsKey(v) || defMap[v] != def) {
      changed = true;
      defMap[v] = def;
    }
  }

  void AddAssignment(AssignLhs lhs, Expr rhs) {
    if (lhs is SimpleAssignLhs) {
      var sLhs = (SimpleAssignLhs)lhs;
      var theVar = sLhs.DeepAssignedVariable;
      if ((defMap.ContainsKey(theVar) && defMap[theVar] != rhs) || !IsConstant(rhs)) {
        UpdateDefMap(theVar, null);
      } else {
        UpdateDefMap(theVar, rhs);
      }
    }
  }

  void Analyse() {
    do {
      changed = false;
      foreach (var c in verifier.RootRegion(impl).Cmds()) {
        if (c is AssignCmd) {
          var aCmd = (AssignCmd)c;
          foreach (var a in aCmd.Lhss.Zip(aCmd.Rhss)) {
            AddAssignment(a.Item1, a.Item2);
          }
        }
      }
    } while (changed);
  }

  private class BuildNamedDefVisitor : StandardVisitor {
    private VariableDefinitionAnalysis analysis;

    public BuildNamedDefVisitor(VariableDefinitionAnalysis a) {
      analysis = a;
    }

    public override Expr VisitIdentifierExpr(IdentifierExpr expr) {
      if (expr.Decl is Constant)
        return expr;
      return analysis.BuildNamedDefFor(expr.Decl);
    }
  }

  Expr BuildNamedDefFor(Variable v) {
    Expr def;
    if (namedDefMap.TryGetValue(v.Name, out def))
      return def;
    def = (Expr)new BuildNamedDefVisitor(this).Visit(defMap[v].Clone());
    namedDefMap[v.Name] = def;
    return def;
  }

  void BuildNamedDefMap() {
    foreach (var v in defMap.Keys)
      if (defMap[v] != null)
        BuildNamedDefFor(v);
  }

  private class SubstDualisedDefVisitor : StandardVisitor {
    private VariableDefinitionAnalysis analysis;
    private VariableDualiser dualiser;
    public bool isSubstitutable = true;

    public SubstDualisedDefVisitor(VariableDefinitionAnalysis a, int id, string procName) {
      analysis = a;
      dualiser = new VariableDualiser(id, analysis.verifier.uniformityAnalyser, procName);
    }

    public override Expr VisitIdentifierExpr(IdentifierExpr expr) {
      if (expr.Decl is Constant)
        return dualiser.VisitIdentifierExpr(expr);
      var varName = GPUVerifier.StripThreadIdentifier(expr.Decl.Name);
      Expr def;
      if (!analysis.namedDefMap.TryGetValue(varName, out def)) {
        isSubstitutable = false;
        return null;
      }
      return (Expr)dualiser.Visit(def.Clone());
    }
  }

  public Expr SubstDualisedDefinitions(Expr e, int id, string procName) {
    var v = new SubstDualisedDefVisitor(this, id, procName);
    Expr result = (Expr)v.Visit(e.Clone());
    if (!v.isSubstitutable)
      return null;
    return result;
  }

  public static VariableDefinitionAnalysis Analyse(GPUVerifier verifier, Implementation impl) {
    var a = new VariableDefinitionAnalysis(verifier, impl);
    a.Analyse();
    a.BuildNamedDefMap();
    a.defMap = null;
    return a;
  }

}

}