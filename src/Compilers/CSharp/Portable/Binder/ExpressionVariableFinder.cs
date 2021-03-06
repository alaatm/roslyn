﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Roslyn.Utilities;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal class ExpressionVariableFinder : CSharpSyntaxWalker
    {
        private Binder _scopeBinder;
        private Binder _enclosingBinderOpt;
        private ArrayBuilder<LocalSymbol> _localsBuilder;

        internal static void FindExpressionVariables(
            Binder scopeBinder,
            ArrayBuilder<LocalSymbol> builder,
            CSharpSyntaxNode node,
            Binder enclosingBinderOpt = null)
        {
            if (node == null)
            {
                return;
            }

            var finder = s_poolInstance.Allocate();
            finder._scopeBinder = scopeBinder;
            finder._enclosingBinderOpt = enclosingBinderOpt;
            finder._localsBuilder = builder;

            finder.Visit(node);

            finder._scopeBinder = null;
            finder._enclosingBinderOpt = null;
            finder._localsBuilder = null;
            s_poolInstance.Free(finder);
        }

        internal static void FindExpressionVariables(
            Binder binder,
            ArrayBuilder<LocalSymbol> builder,
            SeparatedSyntaxList<ExpressionSyntax> nodes)
        {
            if (nodes.Count == 0)
            {
                return;
            }

            var finder = s_poolInstance.Allocate();
            finder._scopeBinder = binder;
            finder._localsBuilder = builder;

            foreach (var n in nodes)
            {
                finder.Visit(n);
            }

            finder._scopeBinder = null;
            finder._localsBuilder = null;
            s_poolInstance.Free(finder);
        }

        public override void VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            foreach (var decl in node.Declaration.Variables)
            {
                Visit(decl.Initializer?.Value);
            }
        }

        public override void VisitSwitchSection(SwitchSectionSyntax node)
        {
            foreach (var label in node.Labels)
            {
                var match = label as CasePatternSwitchLabelSyntax;
                if (match != null)
                {
                    Visit(match.Pattern);
                    if (match.WhenClause != null)
                    {
                        Visit(match.WhenClause.Condition);
                    }
                }
            }
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            Visit(node.Condition);
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            Visit(node.Condition);
        }

        public override void VisitLockStatement(LockStatementSyntax node)
        {
            Visit(node.Expression);
        }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            Visit(node.Condition);
        }

        public override void VisitSwitchStatement(SwitchStatementSyntax node)
        {
            Visit(node.Expression);
        }

        public override void VisitDeclarationPattern(DeclarationPatternSyntax node)
        {
            _localsBuilder.Add(SourceLocalSymbol.MakeLocal(_scopeBinder.ContainingMemberOrLambda, _scopeBinder, false, node.Type, node.Identifier, LocalDeclarationKind.PatternVariable));
            base.VisitDeclarationPattern(node);
        }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) { }
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) { }
        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) { }

        public override void VisitQueryExpression(QueryExpressionSyntax node)
        {
            // Variables declared in [in] expressions of top level from clause and 
            // join clauses are in scope 
            Visit(node.FromClause.Expression);
            Visit(node.Body);
        }

        public override void VisitQueryBody(QueryBodySyntax node)
        {
            // Variables declared in [in] expressions of top level from clause and 
            // join clauses are in scope 
            foreach (var clause in node.Clauses)
            {
                if (clause.Kind() == SyntaxKind.JoinClause)
                {
                    Visit(((JoinClauseSyntax)clause).InExpression);
                }
            }

            Visit(node.Continuation);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            // The binary operators (except ??) are left-associative, and expressions of the form
            // a + b + c + d .... are relatively common in machine-generated code. The parser can handle
            // creating a deep-on-the-left syntax tree no problem, and then we promptly blow the stack during
            // semantic analysis. Here we build an explicit stack to handle left recursion.

            var operands = ArrayBuilder<ExpressionSyntax>.GetInstance();
            ExpressionSyntax current = node;
            do
            {
                var binOp = (BinaryExpressionSyntax)current;
                operands.Push(binOp.Right);
                current = binOp.Left;
            }
            while (current is BinaryExpressionSyntax);

            Visit(current);
            while (operands.Count > 0)
            {
                Visit(operands.Pop());
            }

            operands.Free();
        }

        public override void VisitDeclarationExpression(DeclarationExpressionSyntax node)
        {
            var context = node?.Parent  // ArgumentSyntax
                              ?.Parent  // ArgumentListSyntax
                              ?.Parent; // invocation/constructor initializer
            switch (context?.Kind())
            {
                case SyntaxKind.InvocationExpression:
                case SyntaxKind.ObjectCreationExpression:
                case SyntaxKind.ThisConstructorInitializer:
                case SyntaxKind.BaseConstructorInitializer:
                    var local = SourceLocalSymbol.MakeOutVariable(_scopeBinder.ContainingMemberOrLambda, _scopeBinder, _enclosingBinderOpt, node.Type(), node.Identifier(), context);
                    _localsBuilder.Add(local);
                    break;
                default:

                    // It looks like we are deling with a syntax tree that has a shape that could never be
                    // produced by the LanguageParser, including all error conditions. 
                    // Out Variable declarations can only appear in an argument list of the syntax nodes mentioned above.
                    throw ExceptionUtilities.UnexpectedValue(context?.Kind());
            }
        }

        #region pool
        private static readonly ObjectPool<ExpressionVariableFinder> s_poolInstance = CreatePool();

        public static ObjectPool<ExpressionVariableFinder> CreatePool()
        {
            return new ObjectPool<ExpressionVariableFinder>(() => new ExpressionVariableFinder(), 10);
        }
        #endregion
    }
}
