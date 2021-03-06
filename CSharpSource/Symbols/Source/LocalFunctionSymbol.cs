﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Microsoft.Cci;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    internal sealed class LocalFunctionSymbol : MethodSymbol
    {

        private readonly Binder _binder;
        private readonly LocalFunctionStatementSyntax _syntax;
        private readonly Symbol _containingSymbol;
        private readonly DeclarationModifiers _declarationModifiers;
        private readonly ImmutableArray<LocalFunctionTypeParameterSymbol> _typeParameters;
        private readonly RefKind _refKind;

        private ImmutableArray<ParameterSymbol> _lazyParameters;
        private bool _lazyIsVarArg;
        private ImmutableArray<TypeParameterConstraintClause> _lazyTypeParameterConstraints;
        private TypeSymbol _lazyReturnType;
        private TypeSymbol _iteratorElementType;

        // Lock for initializing lazy fields and registering their diagnostics
        // Acquire this lock when initializing lazy objects to guarantee their declaration
        // diagnostics get added to the store exactly once
        private readonly DiagnosticBag _declarationDiagnostics;

        public LocalFunctionSymbol(
            Binder binder,
            Symbol containingSymbol,
            LocalFunctionStatementSyntax syntax)
        {
            _syntax = syntax;
            _containingSymbol = containingSymbol;

            _declarationModifiers =
                DeclarationModifiers.Private |
                DeclarationModifiers.Static |
                syntax.Modifiers.ToDeclarationModifiers();

            ScopeBinder = binder;

            binder = binder.WithUnsafeRegionIfNecessary(syntax.Modifiers);

            _declarationDiagnostics = new DiagnosticBag();

            if (_syntax.TypeParameterList != null)
            {
                binder = new WithMethodTypeParametersBinder(this, binder);
                _typeParameters = MakeTypeParameters(_declarationDiagnostics);
            }
            else
            {
                _typeParameters = ImmutableArray<LocalFunctionTypeParameterSymbol>.Empty;
                ReportErrorIfHasConstraints(_syntax.ConstraintClauses, _declarationDiagnostics);
            }

            if (IsExtensionMethod)
            {
                _declarationDiagnostics.Add(ErrorCode.ERR_BadExtensionAgg, Locations[0]);
            }

            foreach (var param in syntax.ParameterList.Parameters)
            {
                ReportAttributesDisallowed(param.AttributeLists, _declarationDiagnostics);
            }

            _binder = binder;
            _refKind = (syntax.ReturnType.Kind() == SyntaxKind.RefType) ? RefKind.Ref : RefKind.None;
        }

        /// <summary>
        /// Binder that owns the scope for the local function symbol, namely the scope where the
        /// local function is declared.
        /// </summary>
        internal Binder ScopeBinder { get; }

        public Binder ParameterBinder => _binder;

        internal void GetDeclarationDiagnostics(DiagnosticBag addTo)
        {
            // Force complete type parameters
            foreach (var typeParam in _typeParameters)
            {
                typeParam.ForceComplete(null, default(CancellationToken));
            }

            // force lazy init
            ComputeParameters();

            foreach (var p in _lazyParameters)
            {
                // Force complete parameters to retrieve all diagnostics
                p.ForceComplete(null, default(CancellationToken));
            }

            ComputeReturnType();

            addTo.AddRange(_declarationDiagnostics);
        }

        internal override void AddDeclarationDiagnostics(DiagnosticBag diagnostics)
            => _declarationDiagnostics.AddRange(diagnostics);

        public override bool IsVararg
        {
            get
            {
                ComputeParameters();
                return _lazyIsVarArg;
            }
        }

        public override ImmutableArray<ParameterSymbol> Parameters
        {
            get
            {
                ComputeParameters();
                return _lazyParameters;
            }
        }

        private void ComputeParameters()
        {
            if (_lazyParameters != null)
            {
                return;
            }

            SyntaxToken arglistToken;
            var diagnostics = DiagnosticBag.GetInstance();

            var parameters = ParameterHelpers.MakeParameters(
                _binder,
                this,
                _syntax.ParameterList,
                arglistToken: out arglistToken,
                allowRefOrOut: true,
                allowThis: true,
                diagnostics: diagnostics);

            var isVararg = arglistToken.Kind() == SyntaxKind.ArgListKeyword;
            if (isVararg)
            {
                diagnostics.Add(ErrorCode.ERR_IllegalVarArgs, arglistToken.GetLocation());
            }

            if (IsAsync)
            {
                SourceMemberMethodSymbol.ReportAsyncParameterErrors(parameters, diagnostics, this.Locations[0]);
            }

            lock (_declarationDiagnostics)
            {
                if (_lazyParameters != null)
                {
                    diagnostics.Free();
                    return;
                }

                _declarationDiagnostics.AddRangeAndFree(diagnostics);
                _lazyIsVarArg = isVararg;
                _lazyParameters = parameters;
            }
        }

        public override TypeSymbol ReturnType
        {
            get
            {
                ComputeReturnType();
                return _lazyReturnType;
            }
        }

        internal override RefKind RefKind
        {
            get
            {
                return _refKind;
            }
        }

        internal void ComputeReturnType()
        {
            if (_lazyReturnType != null)
            {
                return;
            }

            var diagnostics = DiagnosticBag.GetInstance();
            RefKind refKind;
            TypeSyntax returnTypeSyntax = _syntax.ReturnType.SkipRef(out refKind);
            TypeSymbol returnType = _binder.BindType(returnTypeSyntax, diagnostics);
            if (IsAsync &&
                returnType.SpecialType != SpecialType.System_Void &&
                !returnType.IsNonGenericTaskType(_binder.Compilation) &&
                !returnType.IsGenericTaskType(_binder.Compilation))
            {
                // The return type of an async method must be void, Task or Task<T>
                diagnostics.Add(ErrorCode.ERR_BadAsyncReturn, this.Locations[0]);
            }

            Debug.Assert(refKind == RefKind.None
                || returnType.SpecialType != SpecialType.System_Void
                || returnTypeSyntax.HasErrors);

            lock (_declarationDiagnostics)
            {
                if (_lazyReturnType != null)
                {
                    diagnostics.Free();
                    return;
                }

                _declarationDiagnostics.AddRangeAndFree(diagnostics);
                _lazyReturnType = returnType;
            }
        }

        public override bool ReturnsVoid => ReturnType?.SpecialType == SpecialType.System_Void;

        public override int Arity => TypeParameters.Length;

        public override ImmutableArray<TypeSymbol> TypeArguments => TypeParameters.Cast<TypeParameterSymbol, TypeSymbol>();

        public override ImmutableArray<TypeParameterSymbol> TypeParameters 
            => _typeParameters.Cast<LocalFunctionTypeParameterSymbol, TypeParameterSymbol>();

        public override bool IsExtensionMethod
        {
            get
            {
                // It is an error to be an extension method, but we need to compute it to report it
                var firstParam = _syntax.ParameterList.Parameters.FirstOrDefault();
                return firstParam != null &&
                    !firstParam.IsArgList &&
                    firstParam.Modifiers.Any(SyntaxKind.ThisKeyword);
            }
        }

        internal override TypeSymbol IteratorElementType
        {
            get
            {
                return _iteratorElementType;
            }
            set
            {
                Debug.Assert((object)_iteratorElementType == null || _iteratorElementType == value);
                Interlocked.CompareExchange(ref _iteratorElementType, value, null);
            }
        }

        public override MethodKind MethodKind => MethodKind.LocalFunction;

        public sealed override Symbol ContainingSymbol => _containingSymbol;

        public override string Name => _syntax.Identifier.ValueText;

        public SyntaxToken NameToken => _syntax.Identifier;

        public Binder SignatureBinder => _binder;

        internal override bool HasSpecialName => false;

        public override bool HidesBaseMethodsByName => false;

        public override ImmutableArray<MethodSymbol> ExplicitInterfaceImplementations => ImmutableArray<MethodSymbol>.Empty;

        public override ImmutableArray<Location> Locations => ImmutableArray.Create(_syntax.Identifier.GetLocation());

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => ImmutableArray.Create(_syntax.GetReference());

        internal override bool GenerateDebugInfo => true;

        public override ImmutableArray<CustomModifier> ReturnTypeCustomModifiers => ImmutableArray<CustomModifier>.Empty;

        public override ImmutableArray<CustomModifier> RefCustomModifiers => ImmutableArray<CustomModifier>.Empty;

        internal override MethodImplAttributes ImplementationAttributes => default(MethodImplAttributes);

        internal override ObsoleteAttributeData ObsoleteAttributeData => null;

        internal override MarshalPseudoCustomAttributeData ReturnValueMarshallingInformation => null;

        internal override CallingConvention CallingConvention => CallingConvention.Default;

        internal override bool HasDeclarativeSecurity => false;

        internal override bool RequiresSecurityObject => false;

        public override Symbol AssociatedSymbol => null;

        public override Accessibility DeclaredAccessibility => ModifierUtils.EffectiveAccessibility(_declarationModifiers);

        public override bool IsAsync => (_declarationModifiers & DeclarationModifiers.Async) != 0;

        public override bool IsStatic => (_declarationModifiers & DeclarationModifiers.Static) != 0;

        public override bool IsVirtual => (_declarationModifiers & DeclarationModifiers.Virtual) != 0;

        public override bool IsOverride => (_declarationModifiers & DeclarationModifiers.Override) != 0;

        public override bool IsAbstract => (_declarationModifiers & DeclarationModifiers.Abstract) != 0;

        public override bool IsSealed => (_declarationModifiers & DeclarationModifiers.Sealed) != 0;

        public override bool IsExtern => (_declarationModifiers & DeclarationModifiers.Extern) != 0;

        public bool IsUnsafe => (_declarationModifiers & DeclarationModifiers.Unsafe) != 0;

        internal bool IsExpressionBodied => _syntax.Body == null && _syntax.ExpressionBody != null;

        public override DllImportData GetDllImportData() => null;

        internal override ImmutableArray<string> GetAppliedConditionalSymbols() => ImmutableArray<string>.Empty;

        internal override bool IsMetadataNewSlot(bool ignoreInterfaceImplementationChanges = false) => false;

        internal override bool IsMetadataVirtual(bool ignoreInterfaceImplementationChanges = false) => false;

        internal override IEnumerable<SecurityAttribute> GetSecurityInformation()
        {
            throw ExceptionUtilities.Unreachable;
        }

        internal override int CalculateLocalSyntaxOffset(int localPosition, SyntaxTree localTree)
        {
            throw ExceptionUtilities.Unreachable;
        }

        internal override bool TryGetThisParameter(out ParameterSymbol thisParameter)
        {
            // Local function symbols have no "this" parameter
            thisParameter = null;
            return true;
        }

        private static void ReportAttributesDisallowed(SyntaxList<AttributeListSyntax> attributes, DiagnosticBag diagnostics)
        {
            foreach (var attrList in attributes)
            {
                diagnostics.Add(ErrorCode.ERR_AttributesInLocalFuncDecl, attrList.Location);
            }
        }

        private ImmutableArray<LocalFunctionTypeParameterSymbol> MakeTypeParameters(DiagnosticBag diagnostics)
        {
            var result = ArrayBuilder<LocalFunctionTypeParameterSymbol>.GetInstance();
            var typeParameters = _syntax.TypeParameterList.Parameters;
            for (int ordinal = 0; ordinal < typeParameters.Count; ordinal++)
            {
                var parameter = typeParameters[ordinal];
                if (parameter.VarianceKeyword.Kind() != SyntaxKind.None)
                {
                    diagnostics.Add(ErrorCode.ERR_IllegalVarianceSyntax, parameter.VarianceKeyword.GetLocation());
                }

                // Attributes are currently disallowed on local function type parameters
                ReportAttributesDisallowed(parameter.AttributeLists, diagnostics);

                var identifier = parameter.Identifier;
                var location = identifier.GetLocation();
                var name = identifier.ValueText;

                foreach (var @param in result)
                {
                    if (name == @param.Name)
                    {
                        diagnostics.Add(ErrorCode.ERR_DuplicateTypeParameter, location, name);
                        break;
                    }
                }

                var tpEnclosing = ContainingSymbol.FindEnclosingTypeParameter(name);
                if ((object)tpEnclosing != null)
                {
                    // Type parameter '{0}' has the same name as the type parameter from outer type '{1}'
                    diagnostics.Add(ErrorCode.WRN_TypeParameterSameAsOuterTypeParameter, location, name, tpEnclosing.ContainingSymbol);
                }

                var typeParameter = new LocalFunctionTypeParameterSymbol(
                        this,
                        name,
                        ordinal,
                        ImmutableArray.Create(location),
                        ImmutableArray.Create(parameter.GetReference()));

                result.Add(typeParameter);
            }

            return result.ToImmutableAndFree();
        }

        internal TypeParameterConstraintKind GetTypeParameterConstraints(int ordinal)
        {
            var clause = this.GetTypeParameterConstraintClause(ordinal);
            return (clause != null) ? clause.Constraints : TypeParameterConstraintKind.None;
        }

        internal ImmutableArray<TypeSymbol> GetTypeParameterConstraintTypes(int ordinal)
        {
            var clause = this.GetTypeParameterConstraintClause(ordinal);
            return (clause != null) ? clause.ConstraintTypes : ImmutableArray<TypeSymbol>.Empty;
        }

        private TypeParameterConstraintClause GetTypeParameterConstraintClause(int ordinal)
        {
            if (_lazyTypeParameterConstraints == null)
            {
                var diagnostics = DiagnosticBag.GetInstance();
                var constraints = MakeTypeParameterConstraints(diagnostics);

                lock (_declarationDiagnostics)
                {
                    if (_lazyTypeParameterConstraints == null)
                    {
                        _declarationDiagnostics.AddRange(diagnostics);
                        _lazyTypeParameterConstraints = constraints;
                    }
                }
                diagnostics.Free();
            }

            var clauses = _lazyTypeParameterConstraints;
            return (clauses.Length > 0) ? clauses[ordinal] : null;
        }

        private ImmutableArray<TypeParameterConstraintClause> MakeTypeParameterConstraints(DiagnosticBag diagnostics)
        {
            var typeParameters = this.TypeParameters;
            if (typeParameters.Length == 0)
            {
                return ImmutableArray<TypeParameterConstraintClause>.Empty;
            }

            var constraintClauses = _syntax.ConstraintClauses;
            if (constraintClauses.Count == 0)
            {
                return ImmutableArray<TypeParameterConstraintClause>.Empty;
            }

            var syntaxTree = _syntax.SyntaxTree;

            // Wrap binder from factory in a generic constraints specific binder
            // to avoid checking constraints when binding type names.
            Debug.Assert(!_binder.Flags.Includes(BinderFlags.GenericConstraintsClause));
            var binder = _binder.WithAdditionalFlags(BinderFlags.GenericConstraintsClause | BinderFlags.SuppressConstraintChecks);

            var result = binder.BindTypeParameterConstraintClauses(this, typeParameters, constraintClauses, diagnostics);
            this.CheckConstraintTypesVisibility(new SourceLocation(_syntax.Identifier), result, diagnostics);
            return result;
        }

        public override int GetHashCode()
        {
            // this is what lambdas do (do not use hashes of other fields)
            return _syntax.GetHashCode();
        }

        public sealed override bool Equals(object symbol)
        {
            if ((object)this == symbol) return true;

            var localFunction = symbol as LocalFunctionSymbol;
            return (object)localFunction != null
                && localFunction._syntax == _syntax;
        }
    }
}
