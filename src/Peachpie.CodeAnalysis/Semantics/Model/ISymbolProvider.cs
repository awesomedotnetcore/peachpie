﻿using Devsense.PHP.Syntax;
using Microsoft.CodeAnalysis;
using Pchp.CodeAnalysis.Symbols;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Pchp.CodeAnalysis.Semantics
{
    /// <summary>
    /// Represents PHP semantics.
    /// Used to query semantic questions about the compilation in specific context.
    /// </summary>
    /// <remarks>Use <see cref="SemanticModel"/> once we implement <see cref="SyntaxTree"/>.</remarks>
    internal interface ISymbolProvider
    {
        /// <summary>
        /// Gets a file by its path relative to current context.
        /// </summary>
        IPhpScriptTypeSymbol ResolveFile(string path);

        /// <summary>
        /// Gets type symbol by its name in current context.
        /// Can be <c>null</c> if type cannot be found.
        /// </summary>
        INamedTypeSymbol ResolveType(QualifiedName name);

        /// <summary>
        /// Get global function symbol by its name in current context.
        /// Can be <c>null</c> if function could not be found.
        /// </summary>
        IPhpRoutineSymbol ResolveFunction(QualifiedName name);

        /// <summary>
        /// Resolves single global constant valid in current context.
        /// </summary>
        IPhpValue ResolveConstant(string name);

        /// <summary>
        /// Gets enumeration of referenced extensions.
        /// </summary>
        IEnumerable<string> Extensions { get; }
    }
}
