﻿#region CodeMaid is Copyright 2007-2011 Steve Cadwallader.

// CodeMaid is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License version 3
// as published by the Free Software Foundation.
//
// CodeMaid is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details <http://www.gnu.org/licenses/>.

#endregion CodeMaid is Copyright 2007-2011 Steve Cadwallader.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EnvDTE;
using SteveCadwallader.CodeMaid.CodeItems;

namespace SteveCadwallader.CodeMaid.Helpers
{
    /// <summary>
    /// A helper class for cleaning up code.
    /// </summary>
    /// <remarks>
    /// Note:  All text replacements search against '\n' but insert/replace
    ///        with Environment.NewLine.  This handles line endings correctly.
    /// </remarks>
    internal class CodeCleanupHelper
    {
        #region Constructors

        /// <summary>
        /// The singleton instance of the <see cref="CodeCleanupHelper"/> class.
        /// </summary>
        private static CodeCleanupHelper _instance;

        /// <summary>
        /// Gets an instance of the <see cref="CodeCleanupHelper"/> class.
        /// </summary>
        /// <param name="package">The hosting package.</param>
        /// <returns>An instance of the <see cref="CodeCleanupHelper"/> class.</returns>
        internal static CodeCleanupHelper GetInstance(CodeMaidPackage package)
        {
            return _instance ?? (_instance = new CodeCleanupHelper(package));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CodeCleanupHelper"/> class.
        /// </summary>
        /// <param name="package">The hosting package.</param>
        private CodeCleanupHelper(CodeMaidPackage package)
        {
            Package = package;

            CodeCleanupAvailabilityHelper = CodeCleanupAvailabilityHelper.GetInstance(Package);
        }

        #endregion Constructors

        #region Internal Methods

        /// <summary>
        /// Attempts to run code cleanup on the specified project item.
        /// </summary>
        /// <param name="projectItem">The project item for cleanup.</param>
        /// <returns>True if cleanup successfully ran, false otherwise.</returns>
        internal bool Cleanup(ProjectItem projectItem)
        {
            if (!CodeCleanupAvailabilityHelper.ShouldCleanup(projectItem))
            {
                return false;
            }

            // Attempt to open the document if not already opened.
            bool wasOpen = projectItem.get_IsOpen(Constants.vsViewKindTextView);
            if (!wasOpen)
            {
                try
                {
                    projectItem.Open(Constants.vsViewKindTextView);
                }
                catch (Exception)
                {
                    // OK if file cannot be opened (ex: deleted from disk, non-text based type.)
                }
            }

            if (projectItem.Document != null)
            {
                bool result = Cleanup(projectItem.Document, false);

                // Close the document if it was opened for cleanup.
                if (Package.Options.CleanupGeneral.AutoCloseIfOpenedByCleanup && !wasOpen)
                {
                    projectItem.Document.Close(vsSaveChanges.vsSaveChangesYes);
                }

                return result;
            }

            return false;
        }

        /// <summary>
        /// Attempts to run code cleanup on the specified document.
        /// </summary>
        /// <param name="document">The document for cleanup.</param>
        /// <param name="isAutoSave">A flag indicating if occurring due to auto-save.</param>
        /// <returns>True if cleanup successfully ran, false otherwise.</returns>
        internal bool Cleanup(Document document, bool isAutoSave)
        {
            if (!CodeCleanupAvailabilityHelper.ShouldCleanup(document))
            {
                return false;
            }

            // Make sure the document to be cleaned up is active, required for some commands like format document.
            document.Activate();

            // Conditionally start an undo transaction (unless auto-saving, configuration option disabled or inside one already).
            bool shouldCloseUndoContext = false;
            if (!isAutoSave && Package.Options.CleanupGeneral.WrapCleanupInASingleUndoTransaction && !Package.IDE.UndoContext.IsOpen)
            {
                Package.IDE.UndoContext.Open("CodeMaid Cleanup", false);
                shouldCloseUndoContext = true;
            }

            try
            {
                var cleanupMethod = FindCodeCleanupMethod(document);
                if (cleanupMethod != null)
                {
                    Package.IDE.StatusBar.Text = String.Format("CodeMaid is cleaning '{0}'...", document.Name);

                    // Perform the set of configured cleanups based on the language.
                    cleanupMethod(document, isAutoSave);

                    Package.IDE.StatusBar.Text = String.Format("CodeMaid cleaned '{0}'.", document.Name);
                }

                return true;
            }
            catch (Exception ex)
            {
                Package.IDE.StatusBar.Text = String.Format("CodeMaid stopped cleaning '{0}': {1}", document.Name, ex);
                if (shouldCloseUndoContext)
                {
                    Package.IDE.UndoContext.SetAborted();
                    shouldCloseUndoContext = false;
                }
                return false;
            }
            finally
            {
                // Always close the undo transaction to prevent ongoing interference with the IDE.
                if (shouldCloseUndoContext)
                {
                    Package.IDE.UndoContext.Close();
                }
            }
        }

        #endregion Internal Methods

        #region Private Language Methods

        /// <summary>
        /// Finds a code cleanup method appropriate for the specified document, otherwise null.
        /// </summary>
        /// <param name="document">The document.</param>
        /// <returns>The code cleanup method, otherwise null.</returns>
        private Action<Document, bool> FindCodeCleanupMethod(Document document)
        {
            switch (document.Language)
            {
                case "CSharp":
                    return RunCodeCleanupCSharp;

                case "C/C++":
                case "CSS":
                case "JScript":
                    return RunCodeCleanupC;

                case "HTML":
                case "XAML":
                case "XML":
                    return RunCodeCleanupGeneric;

                default:
                    System.Diagnostics.Trace.WriteLine(String.Format(
                        "CodeMaid does not support document language '{0}'.", document.Language));
                    return null;
            }
        }

        /// <summary>
        /// Attempts to run code cleanup on the specified CSharp document.
        /// </summary>
        /// <param name="document">The document for cleanup.</param>
        /// <param name="isAutoSave">A flag indicating if occurring due to auto-save.</param>
        private void RunCodeCleanupCSharp(Document document, bool isAutoSave)
        {
            TextDocument textDocument = (TextDocument)document.Object("TextDocument");

            // Perform any actions that can modify the file code model first.
            RunVSFormatting(textDocument);
            RemoveUnusedUsingStatements(isAutoSave);
            SortUsingStatements();

            // Interpret the document into a collection of elements.
            var codeItems = CodeModelHelper.RetrieveCodeItemsExcludingRegions(document);

            var usingStatements = codeItems.OfType<CodeItemUsingStatement>().ToList();
            var namespaces = codeItems.OfType<CodeItemNamespace>().ToList();
            var classes = codeItems.OfType<CodeItemClass>().ToList();
            var enumerations = codeItems.OfType<CodeItemEnum>().ToList();
            var methods = codeItems.OfType<CodeItemMethod>().ToList();
            var properties = codeItems.OfType<CodeItemProperty>().ToList();

            // Perform removal cleanup.
            RemoveEOLWhitespace(textDocument);
            RemoveBlankLinesAtTop(textDocument);
            RemoveBlankLinesAtBottom(textDocument);
            RemoveBlankLinesAfterOpeningBrace(textDocument);
            RemoveBlankLinesBeforeClosingBrace(textDocument);
            RemoveMultipleConsecutiveBlankLines(textDocument);

            // Perform insertion cleanup.
            InsertBlankLinePaddingBeforeRegionTags(textDocument);
            InsertBlankLinePaddingAfterRegionTags(textDocument);
            InsertBlankLinePaddingBeforeEndRegionTags(textDocument);
            InsertBlankLinePaddingAfterEndRegionTags(textDocument);
            InsertBlankLinePaddingBeforeUsingStatementBlocks(usingStatements);
            InsertBlankLinePaddingAfterUsingStatementBlocks(usingStatements);
            InsertBlankLinePaddingBeforeNamespaces(namespaces);
            InsertBlankLinePaddingAfterNamespaces(namespaces);
            InsertBlankLinePaddingBeforeClasses(classes);
            InsertBlankLinePaddingAfterClasses(classes);
            InsertBlankLinePaddingBeforeEnumerations(enumerations);
            InsertBlankLinePaddingAfterEnumerations(enumerations);
            InsertBlankLinePaddingBeforeMethods(methods);
            InsertBlankLinePaddingAfterMethods(methods);
            InsertBlankLinePaddingBeforeProperties(properties);
            InsertBlankLinePaddingAfterProperties(properties);
            InsertExplicitAccessModifiersOnClasses(classes);
            InsertExplicitAccessModifiersOnEnumerations(enumerations);
            InsertExplicitAccessModifiersOnMethods(methods);
            InsertExplicitAccessModifiersOnProperties(properties);

            // Perform update cleanup.
            UpdateEndRegionDirectives(textDocument);
        }

        /// <summary>
        /// Attempts to run code cleanup on the specified C/C++ document.
        /// </summary>
        /// <param name="document">The document for cleanup.</param>
        /// <param name="isAutoSave">A flag indicating if occurring due to auto-save.</param>
        private void RunCodeCleanupC(Document document, bool isAutoSave)
        {
            TextDocument textDocument = (TextDocument)document.Object("TextDocument");

            RunVSFormatting(textDocument);

            RemoveEOLWhitespace(textDocument);
            RemoveBlankLinesAtTop(textDocument);
            RemoveBlankLinesAtBottom(textDocument);
            RemoveBlankLinesAfterOpeningBrace(textDocument);
            RemoveBlankLinesBeforeClosingBrace(textDocument);
            RemoveMultipleConsecutiveBlankLines(textDocument);
        }

        /// <summary>
        /// Attempts to run code cleanup on the specified generic document.
        /// </summary>
        /// <param name="document">The document for cleanup.</param>
        /// <param name="isAutoSave">A flag indicating if occurring due to auto-save.</param>
        private void RunCodeCleanupGeneric(Document document, bool isAutoSave)
        {
            TextDocument textDocument = (TextDocument)document.Object("TextDocument");

            RunVSFormatting(textDocument);

            RemoveEOLWhitespace(textDocument);
            RemoveBlankLinesAtTop(textDocument);
            RemoveBlankLinesAtBottom(textDocument);
            RemoveMultipleConsecutiveBlankLines(textDocument);
        }

        #endregion Private Language Methods

        #region Private Cleanup Methods

        /// <summary>
        /// Inserts a blank line before #region tags except where adjacent to a brace
        /// for the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document.</param>
        private void InsertBlankLinePaddingBeforeRegionTags(TextDocument textDocument)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeRegionTags) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @"{[^\n\{]}\n{:b*}\#region",
                @"\1" + Environment.NewLine + Environment.NewLine + @"\2\#region");
        }

        /// <summary>
        /// Inserts a blank line after #region tags except where adjacent to a brace
        /// for the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document.</param>
        private void InsertBlankLinePaddingAfterRegionTags(TextDocument textDocument)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterRegionTags) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @"^{:b*}\#region{.*}\n{.}",
                @"\1\#region\2" + Environment.NewLine + Environment.NewLine + @"\3");
        }

        /// <summary>
        /// Inserts a blank line before #endregion tags except where adjacent to a brace
        /// for the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document.</param>
        private void InsertBlankLinePaddingBeforeEndRegionTags(TextDocument textDocument)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeEndRegionTags) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @"{.}\n{:b*}\#endregion",
                @"\1" + Environment.NewLine + Environment.NewLine + @"\2\#endregion");
        }

        /// <summary>
        /// Inserts a blank line after #endregion tags except where adjacent to a brace
        /// for the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document.</param>
        private void InsertBlankLinePaddingAfterEndRegionTags(TextDocument textDocument)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterEndRegionTags) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @"^{:b*}\#endregion{.*}\n{:b*[^:b\}]}",
                @"\1\#endregion\2" + Environment.NewLine + Environment.NewLine + @"\3");
        }

        /// <summary>
        /// Inserts a blank line before the specified using statements except where adjacent to a brace.
        /// </summary>
        /// <param name="usingStatements">The using statements to pad.</param>
        private void InsertBlankLinePaddingBeforeUsingStatementBlocks(IEnumerable<CodeItemUsingStatement> usingStatements)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeUsingStatementBlocks) return;

            var usingStatementBlocks = GetCodeItemBlocks(usingStatements);
            var usingStatementsThatStartBlocks = (from IEnumerable<CodeItemUsingStatement> block in usingStatementBlocks select block.First());

            InsertBlankLinePaddingBeforeCodeItems(usingStatementsThatStartBlocks);
        }

        /// <summary>
        /// Inserts a blank line after the specified using statements except where adjacent to a brace.
        /// </summary>
        /// <param name="usingStatements">The using statements to pad.</param>
        private void InsertBlankLinePaddingAfterUsingStatementBlocks(IEnumerable<CodeItemUsingStatement> usingStatements)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterUsingStatementBlocks) return;

            var usingStatementBlocks = GetCodeItemBlocks(usingStatements);
            var usingStatementsThatEndBlocks = (from IEnumerable<CodeItemUsingStatement> block in usingStatementBlocks select block.Last());

            InsertBlankLinePaddingAfterCodeItems(usingStatementsThatEndBlocks);
        }

        /// <summary>
        /// Inserts a blank line before the specified namespaces except where adjacent to a brace.
        /// </summary>
        /// <param name="namespaces">The namespaces to pad.</param>
        private void InsertBlankLinePaddingBeforeNamespaces(IEnumerable<CodeItemNamespace> namespaces)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeNamespaces) return;

            InsertBlankLinePaddingBeforeCodeItems(namespaces);
        }

        /// <summary>
        /// Inserts a blank line after the specified namespaces except where adjacent to a brace.
        /// </summary>
        /// <param name="namespaces">The namespaces to pad.</param>
        private void InsertBlankLinePaddingAfterNamespaces(IEnumerable<CodeItemNamespace> namespaces)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterNamespaces) return;

            InsertBlankLinePaddingAfterCodeItems(namespaces);
        }

        /// <summary>
        /// Inserts a blank line before the specified classes except where adjacent to a brace.
        /// </summary>
        /// <param name="classes">The classes to pad.</param>
        private void InsertBlankLinePaddingBeforeClasses(IEnumerable<CodeItemClass> classes)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeClasses) return;

            InsertBlankLinePaddingBeforeCodeItems(classes);
        }

        /// <summary>
        /// Inserts a blank line after the specified classes except where adjacent to a brace.
        /// </summary>
        /// <param name="classes">The classes to pad.</param>
        private void InsertBlankLinePaddingAfterClasses(IEnumerable<CodeItemClass> classes)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterClasses) return;

            InsertBlankLinePaddingAfterCodeItems(classes);
        }

        /// <summary>
        /// Inserts a blank line before the specified enumerations except where adjacent to a brace.
        /// </summary>
        /// <param name="enumerations">The enumerations to pad.</param>
        private void InsertBlankLinePaddingBeforeEnumerations(IEnumerable<CodeItemEnum> enumerations)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeEnumerations) return;

            InsertBlankLinePaddingBeforeCodeItems(enumerations);
        }

        /// <summary>
        /// Inserts a blank line after the specified enumerations except where adjacent to a brace.
        /// </summary>
        /// <param name="enumerations">The enumerations to pad.</param>
        private void InsertBlankLinePaddingAfterEnumerations(IEnumerable<CodeItemEnum> enumerations)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterEnumerations) return;

            InsertBlankLinePaddingAfterCodeItems(enumerations);
        }

        /// <summary>
        /// Inserts a blank line before the specified methods except where adjacent to a brace.
        /// </summary>
        /// <param name="methods">The methods to pad.</param>
        private void InsertBlankLinePaddingBeforeMethods(IEnumerable<CodeItemMethod> methods)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeMethods) return;

            InsertBlankLinePaddingBeforeCodeItems(methods);
        }

        /// <summary>
        /// Inserts a blank line after the specified methods except where adjacent to a brace.
        /// </summary>
        /// <param name="methods">The methods to pad.</param>
        private void InsertBlankLinePaddingAfterMethods(IEnumerable<CodeItemMethod> methods)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterMethods) return;

            InsertBlankLinePaddingAfterCodeItems(methods);
        }

        /// <summary>
        /// Inserts a blank line before the specified properties except where adjacent to a brace.
        /// </summary>
        /// <param name="properties">The properties to pad.</param>
        private void InsertBlankLinePaddingBeforeProperties(IEnumerable<CodeItemProperty> properties)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingBeforeProperties) return;

            InsertBlankLinePaddingBeforeCodeItems(properties);
        }

        /// <summary>
        /// Inserts a blank line after the specified properties except where adjacent to a brace.
        /// </summary>
        /// <param name="properties">The properties to pad.</param>
        private void InsertBlankLinePaddingAfterProperties(IEnumerable<CodeItemProperty> properties)
        {
            if (!Package.Options.CleanupInsert.InsertBlankLinePaddingAfterProperties) return;

            InsertBlankLinePaddingAfterCodeItems(properties);
        }

        /// <summary>
        /// Inserts the explicit access modifiers on classes where they are not specified.
        /// </summary>
        /// <param name="classes">The classes.</param>
        private void InsertExplicitAccessModifiersOnClasses(IEnumerable<CodeItemClass> classes)
        {
            if (!Package.Options.CleanupInsert.InsertExplicitAccessModifiersOnClasses) return;

            foreach (var codeClass in classes.Select(x => x.CodeClass).Where(y => y != null))
            {
                var classDeclaration = CodeModelHelper.GetClassDeclaration(codeClass);

                // Skip partial classes - access modifier may be specified elsewhere.
                if (IsKeywordSpecified(classDeclaration, PartialKeyword))
                {
                    continue;
                }

                if (!IsAccessModifierExplicitlySpecifiedOnCodeElement(classDeclaration, codeClass.Access))
                {
                    // Set the access value to itself to cause the code to be added.
                    codeClass.Access = codeClass.Access;
                }
            }
        }

        /// <summary>
        /// Inserts the explicit access modifiers on enumerations where they are not specified.
        /// </summary>
        /// <param name="enumerations">The enumerations.</param>
        private void InsertExplicitAccessModifiersOnEnumerations(IEnumerable<CodeItemEnum> enumerations)
        {
            if (!Package.Options.CleanupInsert.InsertExplicitAccessModifiersOnEnumerations) return;

            foreach (var codeEnum in enumerations.Select(x => x.CodeEnum).Where(y => y != null))
            {
                var enumDeclaration = CodeModelHelper.GetEnumerationDeclaration(codeEnum);

                if (!IsAccessModifierExplicitlySpecifiedOnCodeElement(enumDeclaration, codeEnum.Access))
                {
                    // Set the access value to itself to cause the code to be added.
                    codeEnum.Access = codeEnum.Access;
                }
            }
        }

        /// <summary>
        /// Inserts the explicit access modifiers on methods where they are not specified.
        /// </summary>
        /// <param name="methods">The methods.</param>
        private void InsertExplicitAccessModifiersOnMethods(IEnumerable<CodeItemMethod> methods)
        {
            if (!Package.Options.CleanupInsert.InsertExplicitAccessModifiersOnMethods) return;

            foreach (var codeFunction in methods.Select(x => x.CodeFunction).Where(y => y != null))
            {
                try
                {
                    // Skip static constructors - they should not have an access modifier.
                    if (codeFunction.IsShared && codeFunction.FunctionKind == vsCMFunction.vsCMFunctionConstructor)
                    {
                        continue;
                    }

                    // Skip destructors - they should not have an access modifier.
                    if (codeFunction.FunctionKind == vsCMFunction.vsCMFunctionDestructor)
                    {
                        continue;
                    }

                    // Skip explicit interface implementations.
                    if (codeFunction.Name.Contains("."))
                    {
                        continue;
                    }

                    // Skip methods defined inside an interface.
                    if (codeFunction.Parent is CodeInterface)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    // Skip this method if unable to analyze.
                    continue;
                }

                var methodDeclaration = CodeModelHelper.GetMethodDeclaration(codeFunction);

                // Skip partial methods - access modifier may be specified elsewhere.
                if (IsKeywordSpecified(methodDeclaration, PartialKeyword))
                {
                    continue;
                }

                if (!IsAccessModifierExplicitlySpecifiedOnCodeElement(methodDeclaration, codeFunction.Access))
                {
                    // Set the access value to itself to cause the code to be added.
                    codeFunction.Access = codeFunction.Access;
                }
            }
        }

        /// <summary>
        /// Inserts the explicit access modifiers on properties where they are not specified.
        /// </summary>
        /// <param name="properties">The properties.</param>
        private void InsertExplicitAccessModifiersOnProperties(IEnumerable<CodeItemProperty> properties)
        {
            if (!Package.Options.CleanupInsert.InsertExplicitAccessModifiersOnProperties) return;

            foreach (var codeProperty in properties.Select(x => x.CodeProperty).Where(y => y != null))
            {
                try
                {
                    // Skip explicit interface implementations.
                    if (codeProperty.Name.Contains("."))
                    {
                        continue;
                    }

                    // Skip properties defined inside an interface.
                    if (codeProperty.Parent is CodeInterface)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    // Skip this property if unable to analyze.
                    continue;
                }

                var propertyDeclaration = CodeModelHelper.GetPropertyDeclaration(codeProperty);

                if (!IsAccessModifierExplicitlySpecifiedOnCodeElement(propertyDeclaration, codeProperty.Access))
                {
                    // Set the access value to itself to cause the code to be added.
                    codeProperty.Access = codeProperty.Access;
                }
            }
        }

        /// <summary>
        /// Run the visual studio built-in remove unused using statements command.
        /// </summary>
        /// <param name="isAutoSave">A flag indicating if occurring due to auto-save.</param>
        private void RemoveUnusedUsingStatements(bool isAutoSave)
        {
            if (!Package.Options.CleanupRemove.RemoveUnusedUsingStatements) return;
            if (isAutoSave && Package.Options.CleanupRemove.RemoveUnusedUsingStatementsExceptDuringAutoCleanupOnSave) return;

            // Requires VS2008 (version 9).
            if (Package.IDEVersion >= 9)
            {
                Package.IDE.ExecuteCommand("Edit.RemoveUnusedUsings", String.Empty);
            }
        }

        /// <summary>
        /// Run the visual studio built-in format document command.
        /// </summary>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void RunVSFormatting(TextDocument textDocument)
        {
            if (!Package.Options.CleanupGeneral.RunVisualStudioFormatDocumentCommand) return;

            try
            {
                using (new CursorPositionRestorer(textDocument))
                {
                    // Run the command.
                    Package.IDE.ExecuteCommand("Edit.FormatDocument", String.Empty);
                }
            }
            catch
            {
                // OK if fails, not available for some file types.
            }
        }

        /// <summary>
        /// Removes blank lines from the bottom of the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void RemoveBlankLinesAtBottom(TextDocument textDocument)
        {
            if (!Package.Options.CleanupRemove.RemoveBlankLinesAtBottom) return;

            EditPoint cursor = textDocument.EndPoint.CreateEditPoint();
            cursor.DeleteWhitespace(vsWhitespaceOptions.vsWhitespaceOptionsVertical);

            // The last blank line may not have been removed, perform the delete more explicitly.
            if (cursor.AtEndOfDocument && cursor.AtStartOfLine && cursor.AtEndOfLine)
            {
                var backCursor = cursor.CreateEditPoint();
                backCursor.CharLeft(1);
                backCursor.Delete(cursor);
            }
        }

        /// <summary>
        /// Removes blank lines from the top of the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void RemoveBlankLinesAtTop(TextDocument textDocument)
        {
            if (!Package.Options.CleanupRemove.RemoveBlankLinesAtTop) return;

            EditPoint cursor = textDocument.StartPoint.CreateEditPoint();
            cursor.DeleteWhitespace(vsWhitespaceOptions.vsWhitespaceOptionsVertical);
        }

        /// <summary>
        /// Removes blank lines after an opening brace.
        /// </summary>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void RemoveBlankLinesAfterOpeningBrace(TextDocument textDocument)
        {
            if (!Package.Options.CleanupRemove.RemoveBlankLinesAfterOpeningBrace) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @"\{{:b*(//.*)*}\n\n",
                @"\{\1" + Environment.NewLine);
        }

        /// <summary>
        /// Removes blank lines before a closing brace.
        /// </summary>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void RemoveBlankLinesBeforeClosingBrace(TextDocument textDocument)
        {
            if (!Package.Options.CleanupRemove.RemoveBlankLinesBeforeClosingBrace) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @"\n\n{:b*}\}",
                Environment.NewLine + @"\1\}");
        }

        /// <summary>
        /// Removes all end of line whitespace from the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void RemoveEOLWhitespace(TextDocument textDocument)
        {
            if (!Package.Options.CleanupRemove.RemoveEndOfLineWhitespace) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @":b+\n",
                Environment.NewLine);
        }

        /// <summary>
        /// Removes multiple consecutive blank lines from the specified text document.
        /// </summary>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void RemoveMultipleConsecutiveBlankLines(TextDocument textDocument)
        {
            if (!Package.Options.CleanupRemove.RemoveMultipleConsecutiveBlankLines) return;

            TextDocumentHelper.SubstituteAllStringMatches(textDocument,
                @"\n\n\n+",
                Environment.NewLine + Environment.NewLine);
        }

        /// <summary>
        /// Run the visual studio built-in sort using statements command.
        /// </summary>
        private void SortUsingStatements()
        {
            if (!Package.Options.CleanupUpdate.SortUsingStatements) return;

            // Requires VS2008 (version 9).
            if (Package.IDEVersion >= 9)
            {
                Package.IDE.ExecuteCommand("Edit.SortUsings", String.Empty);
            }
        }

        /// <summary>
        /// Updates the #endregion directives to match the names of the matching
        /// #region directive and cleans up any unnecessary white space.
        /// </summary>
        /// <remarks>
        /// This code is very similar to the Common region retrieval function, but
        /// since it manipulates the cursors during processing the logic is different
        /// enough to warrant a separate copy of the code.
        /// </remarks>
        /// <param name="textDocument">The text document to cleanup.</param>
        private void UpdateEndRegionDirectives(TextDocument textDocument)
        {
            if (!Package.Options.CleanupUpdate.UpdateEndRegionDirectives) return;

            Stack<String> regionStack = new Stack<string>();
            EditPoint cursor = textDocument.StartPoint.CreateEditPoint();
            TextRanges subGroupMatches = null; // Not used - required for FindPattern.

            // Keep pushing cursor forwards (note ref cursor parameter) until finished.
            while (cursor != null &&
                   cursor.FindPattern(@"^:b*\#", TextDocumentHelper.StandardFindOptions, ref cursor, ref subGroupMatches))
            {
                // Create a pointer to capture the text for this line.
                EditPoint eolCursor = cursor.CreateEditPoint();
                eolCursor.EndOfLine();
                string regionText = cursor.GetText(eolCursor);

                if (regionText.StartsWith("region ")) // Space required by compiler.
                {
                    // Cleanup any whitespace in the region name.
                    string regionName = regionText.Substring(7);
                    string regionNameTrimmed = regionName.Trim();
                    if (regionName != regionNameTrimmed)
                    {
                        cursor.CharRight(7);
                        cursor.Delete(eolCursor);
                        cursor.Insert(regionNameTrimmed);
                    }

                    // Push the parsed region name onto the top of the stack.
                    regionStack.Push(regionNameTrimmed);
                }
                else if (regionText.StartsWith("endregion")) // Space may or may not be present.
                {
                    if (regionStack.Count > 0)
                    {
                        // Do not trim the endRegionName in order to catch whitespace differences.
                        string endRegionName = regionText.Length > 9 ?
                            regionText.Substring(10) : String.Empty;
                        string matchingRegion = regionStack.Pop();

                        // Update if the strings do not match.
                        if (matchingRegion != endRegionName)
                        {
                            cursor.CharRight(9);
                            cursor.Delete(eolCursor);
                            cursor.Insert(" " + matchingRegion);
                        }
                    }
                    else
                    {
                        // This document is improperly formatted, abort.
                        return;
                    }
                }

                // Note: eolCursor may be outdated now if changes have been made.
                cursor.EndOfLine();
            }
        }

        #endregion Private Cleanup Methods

        #region Private Helper Methods

        /// <summary>
        /// Gets the specified code items as unique blocks by consecutive line positioning.
        /// </summary>
        /// <typeparam name="T">The type of the code item.</typeparam>
        /// <param name="codeItems">The code items.</param>
        /// <returns>An enumerable collection of blocks of code items.</returns>
        private static IEnumerable<IList<T>> GetCodeItemBlocks<T>(IEnumerable<T> codeItems)
            where T : BaseCodeItem
        {
            var codeItemBlocks = new List<IList<T>>();
            IList<T> currentBlock = null;

            var orderedCodeItems = codeItems.OrderBy(x => x.StartLine);
            foreach (T codeItem in orderedCodeItems)
            {
                if (currentBlock != null &&
                    (codeItem.StartLine <= currentBlock.Last().EndLine + 1))
                {
                    // This item belongs in the current block, add it.
                    currentBlock.Add(codeItem);
                }
                else
                {
                    // This item starts a new block, create one.
                    currentBlock = new List<T> { codeItem };
                    codeItemBlocks.Add(currentBlock);
                }
            }

            return codeItemBlocks;
        }

        /// <summary>
        /// Inserts a blank line before the specified code items except where adjacent to a brace.
        /// </summary>
        /// <typeparam name="T">The type of the code item.</typeparam>
        /// <param name="codeItems">The code items to pad.</param>
        private static void InsertBlankLinePaddingBeforeCodeItems<T>(IEnumerable<T> codeItems)
            where T : BaseCodeItemElement
        {
            foreach (T codeItem in codeItems.Where(x => x.CodeElement != null))
            {
                EditPoint startPoint = codeItem.CodeElement.StartPoint.CreateEditPoint();

                TextDocumentHelper.InsertBlankLineBeforePoint(startPoint);
            }
        }

        /// <summary>
        /// Inserts a blank line after the specified code items except where adjacent to a brace.
        /// </summary>
        /// <typeparam name="T">The type of the code item.</typeparam>
        /// <param name="codeItems">The code items to pad.</param>
        private static void InsertBlankLinePaddingAfterCodeItems<T>(IEnumerable<T> codeItems)
            where T : BaseCodeItemElement
        {
            foreach (T codeItem in codeItems.Where(x => x.CodeElement != null))
            {
                EditPoint endPoint = codeItem.CodeElement.EndPoint.CreateEditPoint();

                TextDocumentHelper.InsertBlankLineAfterPoint(endPoint);
            }
        }

        /// <summary>
        /// Determines if the access modifier is explicitly defined on the specified code element declaration.
        /// </summary>
        /// <param name="codeElementDeclaration">The code element declaration.</param>
        /// <param name="accessModifier">The access modifier.</param>
        /// <returns>True if access modifier is explicitly specified, otherwise false.</returns>
        private static bool IsAccessModifierExplicitlySpecifiedOnCodeElement(string codeElementDeclaration, vsCMAccess accessModifier)
        {
            string keyword = CodeModelHelper.GetAccessModifierKeyword(accessModifier);

            return IsKeywordSpecified(codeElementDeclaration, keyword);
        }

        /// <summary>
        /// Determines if the specified keyword is present in the specified code element declaration.
        /// </summary>
        /// <param name="codeElementDeclaration">The code element declaration.</param>
        /// <param name="keyword">The keyword.</param>
        /// <returns>True if the keyword is present, otherwise false.</returns>
        private static bool IsKeywordSpecified(string codeElementDeclaration, string keyword)
        {
            string matchString = @"(^|\s)" + keyword + @"\s";

            return Regex.IsMatch(codeElementDeclaration, matchString);
        }

        #endregion Private Helper Methods

        #region Private Constants

        /// <summary>
        /// The string representation of the partial keyword.
        /// </summary>
        private const string PartialKeyword = "partial";

        #endregion Private Constants

        #region Private Properties

        /// <summary>
        /// Gets or sets the code cleanup availability helper.
        /// </summary>
        private CodeCleanupAvailabilityHelper CodeCleanupAvailabilityHelper { get; set; }

        /// <summary>
        /// Gets or sets the hosting package.
        /// </summary>
        private CodeMaidPackage Package { get; set; }

        #endregion Private Properties
    }
}