﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Pchp.CodeAnalysis.Errors;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Semantics.Graph;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Pchp.CodeAnalysis.Symbols;
using Devsense.PHP.Syntax.Ast;

namespace Pchp.CodeAnalysis.FlowAnalysis.Passes
{
    internal partial class DiagnosingVisitor : GraphVisitor
    {
        private readonly DiagnosticBag _diagnostics;
        private SourceRoutineSymbol _routine;

        PhpCompilation DeclaringCompilation => _routine.DeclaringCompilation;

        TypeRefContext TypeCtx => _routine.TypeRefContext;

        #region Scope

        struct Scope
        {
            public enum Kind
            {
                Try, Catch, Finally,
            }

            public bool Contains(BoundBlock b) => b != null && b.Ordinal >= From && b.Ordinal < To;

            public bool IsTryCatch => ScopeKind == Kind.Try || ScopeKind == Kind.Catch || ScopeKind == Kind.Finally;

            public int From, To;
            public Kind ScopeKind;
        }

        List<Scope> _lazyScopes = null;

        /// <summary>
        /// Stores a scope (range) of blocks.
        /// </summary>
        void WithScope(Scope scope)
        {
            if (_lazyScopes == null) _lazyScopes = new List<Scope>();
            _lazyScopes.Add(scope);
        }

        bool IsInScope(Scope.Kind kind) => _lazyScopes != null && _lazyScopes.Any(s => s.ScopeKind == kind && s.Contains(_currentBlock));

        bool IsInTryCatchScope() => _lazyScopes != null && _lazyScopes.Any(s => s.IsTryCatch && s.Contains(_currentBlock));

        #endregion

        void Add(Devsense.PHP.Text.Span span, Devsense.PHP.Errors.ErrorInfo err, params string[] args)
        {
            _diagnostics.Add(DiagnosticBagExtensions.ParserDiagnostic(_routine, span, err, args));
        }

        void CannotInstantiate(IPhpOperation op, string kind, BoundTypeRef t)
        {
            _diagnostics.Add(_routine, op.PhpSyntax, ErrorCode.ERR_CannotInstantiateType, kind, t.ResolvedType);
        }

        public static void Analyse(DiagnosticBag diagnostics, SourceRoutineSymbol routine)
        {
            if (routine.ControlFlowGraph != null)   // non-abstract method
            {
                new DiagnosingVisitor(diagnostics, routine)
                    .VisitCFG(routine.ControlFlowGraph);
            }
        }

        private DiagnosingVisitor(DiagnosticBag diagnostics, SourceRoutineSymbol routine)
        {
            _diagnostics = diagnostics;
            _routine = routine;
        }

        public override void VisitCFG(ControlFlowGraph x)
        {
            Debug.Assert(x == _routine.ControlFlowGraph);

            InitializeReachabilityInfo(x);

            base.VisitCFG(x);

            // analyse missing or redefined labels
            CheckLabels(x.Labels);

            // report unreachable blocks
            CheckUnreachableCode(x);
        }

        void CheckLabels(ControlFlowGraph.LabelBlockState[] labels)
        {
            if (labels == null || labels.Length == 0)
            {
                return;
            }

            for (int i = 0; i < labels.Length; i++)
            {
                var flags = labels[i].Flags;
                if ((flags & ControlFlowGraph.LabelBlockFlags.Defined) == 0)
                {
                    Add(labels[i].LabelSpan, Devsense.PHP.Errors.Errors.UndefinedLabel, labels[i].Label);
                }
                if ((flags & ControlFlowGraph.LabelBlockFlags.Used) == 0)
                {
                    // Warning: label not used
                }
                if ((flags & ControlFlowGraph.LabelBlockFlags.Redefined) != 0)
                {
                    Add(labels[i].LabelSpan, Devsense.PHP.Errors.Errors.LabelRedeclared, labels[i].Label);
                }
            }
        }

        public override void VisitEval(BoundEvalEx x)
        {
            _diagnostics.Add(_routine, new TextSpan(x.PhpSyntax.Span.Start, 4)/*'eval'*/, ErrorCode.INF_EvalDiscouraged);

            base.VisitEval(x);
        }

        public override void VisitArray(BoundArrayEx x)
        {
            if (x.Access.IsNone)
            {
                // The expression is not being read. Did you mean to assign it somewhere?
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_ExpressionNotRead);
            }

            base.VisitArray(x);
        }

        public override void VisitTypeRef(BoundTypeRef typeRef)
        {
            if (typeRef != null)
            {
                if (typeRef.HasClassNameRestriction && typeRef.TypeRef is Devsense.PHP.Syntax.Ast.PrimitiveTypeRef)
                {
                    // error: use of primitive type {0} is misused // primitive type does not make any sense in this context
                    _diagnostics.Add(_routine, typeRef.TypeRef, ErrorCode.ERR_PrimitiveTypeNameMisused, typeRef.TypeRef);
                }
                else
                {
                    CheckUndefinedType(typeRef);
                    base.VisitTypeRef(typeRef);
                }
            }
        }

        public override void VisitNew(BoundNewEx x)
        {
            if (x.TypeRef.ResolvedType.IsValidType())
            {
                if (x.TypeRef.ResolvedType.IsInterfaceType())
                {
                    CannotInstantiate(x, "interface", x.TypeRef);
                }
                else if (x.TypeRef.ResolvedType.IsStatic)
                {
                    CannotInstantiate(x, "static", x.TypeRef);
                }
                else if (x.TypeRef.ResolvedType.IsTraitType())
                {
                    CannotInstantiate(x, "trait", x.TypeRef);
                }
                else // class:
                {
                    // cannot instantiate Closure
                    if (x.TypeRef.ResolvedType == DeclaringCompilation.CoreTypes.Closure)
                    {
                        // Instantiation of '{0}' is not allowed
                        Add(x.TypeRef.TypeRef.Span, Devsense.PHP.Errors.Errors.ClosureInstantiated, x.TypeRef.ResolvedType.Name);
                    }

                    //
                    else if (x.TypeRef.ResolvedType.IsAbstract)
                    {
                        // Cannot instantiate abstract class {0}
                        CannotInstantiate(x, "abstract class", x.TypeRef);
                    }
                }
            }

            base.VisitNew(x);
        }

        public override void VisitReturn(BoundReturnStatement x)
        {
            if (_routine.Syntax is MethodDecl m)
            {
                if (m.Name.Name.IsToStringName)
                {
                    // __tostring() allows only strings to be returned
                    if (x.Returned == null || !IsAllowedToStringReturnType(x.Returned.TypeRefMask))
                    {
                        _diagnostics.Add(_routine, x.PhpSyntax ?? m, ErrorCode.ERR_ToStringMustReturnString, ((IPhpTypeSymbol)_routine.ContainingType).FullName.ToString());
                    }
                }
            }

            // "void" return type hint ?
            if (_routine.SyntaxReturnType is Devsense.PHP.Syntax.Ast.PrimitiveTypeRef pt && pt.PrimitiveTypeName == Devsense.PHP.Syntax.Ast.PrimitiveTypeRef.PrimitiveType.@void)
            {
                if (x.Returned != null)
                {
                    // A void function must not return a value
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.ERR_VoidFunctionCannotReturnValue);
                }
            }

            // do not allow return from "finally" block, not allowed in CLR
            if (x.PhpSyntax != null && IsInScope(Scope.Kind.Finally))
            {
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.ERR_NotYetImplemented, "return from 'finally' block");
            }

            //
            base.VisitReturn(x);
        }

        bool IsAllowedToStringReturnType(TypeRefMask tmask)
        {
            return
                tmask.IsRef ||
                tmask.IsAnyType ||  // dunno
                TypeCtx.IsAString(tmask);

            // anything else (object (even convertible to string), array, number, boolean, ...) is not allowed
        }

        public override void VisitAssign(BoundAssignEx x)
        {
            // Template: <x> = <x>
            if (x.Target is BoundVariableRef lvar && lvar.Variable is BoundLocal lloc &&
                x.Value is BoundVariableRef rvar && rvar.Variable is BoundLocal rloc &&
                lloc.Name == rloc.Name && !string.IsNullOrEmpty(lloc.Name) && x.PhpSyntax != null)
            {
                // Assignment made to same variable
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_AssigningSameVariable);
            }

            //

            base.VisitAssign(x);
        }

        public override void VisitGlobalFunctionCall(BoundGlobalFunctionCall x)
        {
            CheckUndefinedFunctionCall(x);

            // calling indirectly:
            if (x.Name.NameExpression != null)
            {
                // check whether expression can be used as a function callback (must be callable - string, array, object ...)
                if (!TypeHelpers.IsCallable(TypeCtx, x.Name.NameExpression.TypeRefMask))
                {
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.ERR_InvalidFunctionName, TypeCtx.ToString(x.Name.NameExpression.TypeRefMask));
                }
            }

            base.VisitGlobalFunctionCall(x);
        }

        public override void VisitInstanceFunctionCall(BoundInstanceFunctionCall call)
        {
            // TODO: Enable the diagnostic when several problems are solved (such as __call())
            //CheckUndefinedMethodCall(call, call.Instance?.ResultType, call.Name);
            base.VisitInstanceFunctionCall(call);

            // check target type
            CheckMethodCallTargetInstance(call.Instance, call.Name.NameValue.Name.Value);

            // check deprecated
            CheckObsoleteSymbol(call.PhpSyntax, call.TargetMethod);
        }

        public override void VisitStaticFunctionCall(BoundStaticFunctionCall call)
        {
            // TODO: Enable the diagnostic when the __callStatic() method is properly processed during analysis
            //CheckUndefinedMethodCall(call, call.TypeRef?.ResolvedType, call.Name);
            base.VisitStaticFunctionCall(call);

            // check deprecated
            CheckObsoleteSymbol(call.PhpSyntax, call.TargetMethod);
        }

        public override void VisitVariableRef(BoundVariableRef x)
        {
            CheckUninitializedVariableUse(x);
            base.VisitVariableRef(x);
        }

        public override void VisitTemporalVariableRef(BoundTemporalVariableRef x)
        {
            // do not make diagnostics on syntesized variables
        }

        public override void VisitDeclareStatement(BoundDeclareStatement x)
        {
            _diagnostics.Add(
                _routine,
                ((DeclareStmt)x.PhpSyntax).GetDeclareClauseSpan(),
                ErrorCode.WRN_NotYetImplementedIgnored,
                "Declare construct");

            base.VisitDeclareStatement(x);
        }

        public override void VisitAssert(BoundAssertEx x)
        {
            base.VisitAssert(x);

            var args = x.ArgumentsInSourceOrder;

            // check number of parameters
            // check whether it is not always false or always true
            if (args.Length >= 1)
            {
                if (args[0].Value.ConstantValue.EqualsOptional(false.AsOptional()))
                {
                    // always failing
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_AssertAlwaysFail);
                }

                if (TypeCtx.IsAString(args[0].Value.TypeRefMask))
                {
                    // deprecated and not supported
                    _diagnostics.Add(_routine, args[0].Value.PhpSyntax, ErrorCode.WRN_StringAssertionDeprecated);
                }

                if (args.Length > 2)
                {
                    // too many args
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_TooManyArguments);
                }
            }
            else
            {
                // assert() expects at least 1 parameter, 0 given
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_MissingArguments, "assert", 1, 0);
            }
        }

        public override void VisitBinaryExpression(BoundBinaryEx x)
        {
            base.VisitBinaryExpression(x);

            //

            switch (x.Operation)
            {
                case Operations.Div:
                    if (x.Right.IsConstant())
                    {
                        if (x.Right.ConstantValue.IsZero())
                        {
                            Add(x.Right.PhpSyntax.Span, Devsense.PHP.Errors.Warnings.DivisionByZero);
                        }
                    }
                    break;
            }
        }

        void CheckMethodCallTargetInstance(BoundExpression target, string methodName)
        {
            if (target == null)
            {
                // syntax error (?)
                return;
            }

            string nonobjtype = null;

            if (target.ResultType != null)
            {
                switch (target.ResultType.SpecialType)
                {
                    case SpecialType.System_Void:
                    case SpecialType.System_Int32:
                    case SpecialType.System_Int64:
                    case SpecialType.System_String:
                    case SpecialType.System_Boolean:
                        nonobjtype = target.ResultType.GetPhpTypeNameOrNull();
                        break;
                    default:
                        if (target.ResultType == DeclaringCompilation.CoreTypes.PhpString ||
                            target.ResultType == DeclaringCompilation.CoreTypes.PhpArray ||
                            target.ResultType == DeclaringCompilation.CoreTypes.PhpNumber ||
                            target.ResultType == DeclaringCompilation.CoreTypes.PhpResource ||
                            target.ResultType == DeclaringCompilation.CoreTypes.IPhpArray ||
                            target.ResultType == DeclaringCompilation.CoreTypes.IPhpCallable)
                        {
                            nonobjtype = target.ResultType.GetPhpTypeNameOrNull();
                        }
                        break;
                }
            }
            else
            {
                var tmask = target.TypeRefMask;
                if (!tmask.IsAnyType && !tmask.IsRef && !TypeCtx.IsObject(tmask))
                {
                    nonobjtype = TypeCtx.ToString(tmask);
                }
            }

            //
            if (nonobjtype != null)
            {
                _diagnostics.Add(_routine, target.PhpSyntax, ErrorCode.ERR_MethodCalledOnNonObject, methodName ?? "{}", nonobjtype);
            }
        }

        void CheckObsoleteSymbol(LangElement node, Symbol target)
        {
            var obsolete = target?.ObsoleteAttributeData;
            if (obsolete != null)
            {
                _diagnostics.Add(_routine, node, ErrorCode.WRN_SymbolDeprecated, target.Kind.ToString(), target.Name, obsolete.Message);
            }
        }

        private void CheckUndefinedFunctionCall(BoundGlobalFunctionCall x)
        {
            if (x.Name.IsDirect && x.TargetMethod.IsErrorMethodOrNull())
            {
                var errmethod = (ErrorMethodSymbol)x.TargetMethod;
                if (errmethod != null && errmethod.ErrorKind == ErrorMethodKind.Missing)
                {
                    var span = x.PhpSyntax is FunctionCall fnc ? fnc.NameSpan : x.PhpSyntax.Span;
                    _diagnostics.Add(_routine, span.ToTextSpan(), ErrorCode.WRN_UndefinedFunctionCall, x.Name.NameValue.ToString());
                }
            }
        }

        private void CheckUndefinedMethodCall(BoundRoutineCall x, TypeSymbol type, BoundRoutineName name)
        {
            if (name.IsDirect && x.TargetMethod.IsErrorMethodOrNull() && type != null && !type.IsErrorType())
            {
                var span = x.PhpSyntax is FunctionCall fnc ? fnc.NameSpan : x.PhpSyntax.Span;
                _diagnostics.Add(_routine, span.ToTextSpan(), ErrorCode.WRN_UndefinedMethodCall, name.NameValue.ToString(), type.Name);
            }
        }

        private void CheckUninitializedVariableUse(BoundVariableRef x)
        {
            if (x.MaybeUninitialized && !x.Access.IsQuiet && x.PhpSyntax != null)
            {
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_UninitializedVariableUse, x.Name.NameValue.ToString());
            }
        }

        private void CheckUndefinedType(BoundTypeRef typeRef)
        {
            // Ignore indirect types (e.g. $foo = new $className())
            if (typeRef.IsDirect && (typeRef.ResolvedType == null || typeRef.ResolvedType.IsErrorType()))
            {
                var errtype = typeRef.ResolvedType as ErrorTypeSymbol;
                if (errtype != null && errtype.CandidateReason == CandidateReason.Ambiguous)
                {
                    // type is declared but ambiguously,
                    // warning with declaration ambiguity was already reported, we may skip following
                    return;
                }

                if (typeRef.TypeRef is ReservedTypeRef)
                {
                    // unresolved parent, self ?
                }
                else
                {
                    var name = typeRef.TypeRef.QualifiedName?.ToString();
                    _diagnostics.Add(_routine, typeRef.TypeRef, ErrorCode.WRN_UndefinedType, name);
                }
            }
        }

        public override void VisitCFGTryCatchEdge(TryCatchEdge x)
        {
            // remember scopes,
            // .Accept() on BodyBlocks traverses not only the try block but also the rest of the code

            WithScope(new Scope
            {
                ScopeKind = Scope.Kind.Try,
                From = x.BodyBlock.Ordinal,
                To = x.NextBlock.Ordinal
            });

            for (int i = 0; i < x.CatchBlocks.Length; i++)
            {
                WithScope(new Scope
                {
                    ScopeKind = Scope.Kind.Catch,
                    From = x.CatchBlocks[i].Ordinal,
                    To = ((i + 1 < x.CatchBlocks.Length) ? x.CatchBlocks[i + 1] : x.FinallyBlock ?? x.NextBlock).Ordinal,
                });
            }

            if (x.FinallyBlock != null)
            {
                WithScope(new Scope
                {
                    ScopeKind = Scope.Kind.Finally,
                    From = x.FinallyBlock.Ordinal,
                    To = x.NextBlock.Ordinal
                });
            }

            // visit:

            base.VisitCFGTryCatchEdge(x);
        }

        public override void VisitStaticStatement(BoundStaticVariableStatement x)
        {
            base.VisitStaticStatement(x);
        }

        public override void VisitYieldStatement(BoundYieldStatement boundYieldStatement)
        {
            if (IsInTryCatchScope())
            {
                // TODO: Start supporting yielding from exception handling constructs.
                _diagnostics.Add(_routine, boundYieldStatement.PhpSyntax, ErrorCode.ERR_NotYetImplemented, "Yielding from an exception handling construct (try, catch, finally)");
            }
        }
    }
}
