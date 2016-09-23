#region License
/* 
 * Copyright (C) 1999-2016 John K�ll�n.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2, or (at your option)
 * any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; see the file COPYING.  If not, write to
 * the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA.
 */
#endregion

using Reko.Core;
using Reko.Core.Code;
using Reko.Core.Expressions;
using Reko.Core.Lib;
using Reko.Core.Operators;
using Reko.Core.Types;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Diagnostics;
using System.Text;

namespace Reko.Analysis
{
	/// <summary>
	/// Transforms a <see cref="Reko.Core.Procedure"/> to Static Single Assignment
    /// form.
	/// </summary>
    /// <remarks>
    /// EXPERIMENTAL - consult uxmal before using
    /// 
    /// This class implements an SSA algorithm that doesn't require 
    /// calculation of the dominator graph. It is based on the algorithm
    /// described in "Simple and Efficient Construction of Static Single
    /// Assignment Form" by Matthias Braun, Sebastian Buchwald, Sebastian 
    /// Hack, Roland Lei�a, Christoph Mallon, and Andreas Zwinkau. It is
    /// expected that when it is fully implemented, it will take over from 
    /// SsaTransform above.
    /// </remarks>
    public class SsaTransform : InstructionTransformer 
    {
        private IProcessorArchitecture arch;
        private ProgramDataFlow programFlow;
        private IImportResolver importResolver;
        private Block block;
        private Statement stmCur;
        private Dictionary<Block, SsaBlockState> blockstates;
        private SsaState ssa;
        private TransformerFactory factory;
        public readonly HashSet<SsaIdentifier> incompletePhis;
        private HashSet<SsaIdentifier> sidsToRemove;

        public SsaTransform(IProcessorArchitecture arch, Procedure proc, IImportResolver importResolver, ProgramDataFlow programFlow)
        {
            this.arch = arch;
            this.programFlow = programFlow;
            this.importResolver = importResolver;
            this.ssa = new SsaState(proc, null);
            this.blockstates = ssa.Procedure.ControlGraph.Blocks.ToDictionary(k => k, v => new SsaBlockState(v));
            this.factory = new TransformerFactory(this);
            this.incompletePhis = new HashSet<SsaIdentifier>();
        }

        /// <summary>
        /// If set, only renames frame accesses.
        /// </summary>
        public bool RenameFrameAccesses { get; set; }

        /// <summary>
        /// The SSA graph of the procedure being transformed.
        /// </summary>
        public SsaState SsaState { get { return ssa; } }

        /// <summary>
        /// Transforms <paramref name="proc"/> into Static Single
        /// Assignment form.
        /// </summary>
        /// <remarks>
        /// The resulting SSA identifiers are conventiently kept in the
        /// SsaState property.
        /// </remarks>
        /// <param name="proc"></param>
        public SsaState Transform()
        {
            this.sidsToRemove = new HashSet<SsaIdentifier>();

            // Visit blocks in RPO order so that we are guaranteed that a 
            // block with predecessors is always visited after them.

            foreach (Block b in new DfsIterator<Block>(ssa.Procedure.ControlGraph).ReversePostOrder())
            {
                this.block = b;
                //Debug.Print("*** {0}:", b.Name);
                foreach (var s in b.Statements.ToList())
                {
                    this.stmCur = s;
                    //Debug.Print("***  {0}", s.Instruction);
                    s.Instruction = s.Instruction.Accept(this);
                }
                blockstates[b].Visited = true;
            }
            ProcessIncompletePhis();
            return ssa;
        }

        /// <summary>
        /// Remove any SSA identifiers with no uses. There will be a lot
        /// of these, especially for flag registers.
        /// </summary>
        public void RemoveDeadSsaIdentifiers()
        {
            foreach (var sid in sidsToRemove.Where(s => s.Uses.Count == 0))
            {
                ssa.Identifiers.Remove(sid);
            }
        }

        /// <summary>
        /// Adds a UseInstruction for each SsaIdentifier.
        /// </summary>
        /// <remarks>
        /// Doing this will allow us to detect what definitions reach the end
        /// of the function.
        /// //$TODO: what about functions that don't terminate, or have branches that don't terminate? In such cases,
        /// the identifiers should be removed.
        /// </remarks>
        public void AddUsesToExitBlock()
        {
            //$TODO: flag groups need to be grouped on exit
            // TrashedRegisterFinder should collect aliased registers
            // (e.g. eax, ax, al, ah) and render them as a single
            // register (eax).
            this.block = ssa.Procedure.ExitBlock;

            // Compute the set of all blocks b such that there is a path from
            // b to the exit block.
            var reachingBlocks = FindPredecessorClosure(ssa.Procedure.ExitBlock);
            var existing = block.Statements
                .Select(s => s.Instruction as UseInstruction)
                .Where(u => u != null)
                .Select(u => u.Expression)
                .ToHashSet();
            var reachingIds = ssa.Identifiers
                .Where(sid => sid.DefStatement != null &&
                              reachingBlocks.Contains(sid.DefStatement.Block) &&
                              sid.Identifier.Name != sid.OriginalIdentifier.Name &&
                              !(sid.Identifier.Storage is MemoryStorage) &&
                              !(sid.Identifier.Storage is StackStorage) &&
                              !(sid.Identifier.Storage is TemporaryStorage) &&
                              !existing.Contains(sid.Identifier))
                .Select(sid => sid.OriginalIdentifier);
            reachingIds = SeparateSequences(reachingIds);
            reachingIds = ExpandFlags(reachingIds);
            var sortedIds = ResolveOverlaps(reachingIds)
                .Distinct()
                .OrderBy(id => id.Name);    // Sort them for stability; unit test are sensitive to shifting order 

            var stms = sortedIds.Select(id => new Statement(0, new UseInstruction(id), block)).ToList();
            block.Statements.AddRange(stms);
            stms.ForEach(u =>
            {
                var use = (UseInstruction)u.Instruction;
                use.Expression = NewUse((Identifier)use.Expression, u, true);
            });
        }

        private ISet<Block> FindPredecessorClosure(Block start)
        {
            var wl = new WorkList<Block>();
            var preds = new HashSet<Block>();
            wl.Add(start);
            Block b;
            while (wl.GetWorkItem(out b))
            {
                foreach (var p in b.Pred)
                {
                    if (!preds.Contains(p))
                    {
                        preds.Add(p);
                        wl.Add(p);
                    }
                }
            }
            return preds;
        }

        public IEnumerable<Identifier> SeparateSequences(IEnumerable<Identifier> ids)
        {
            foreach (var id in ids)
            {
                var seq = id.Storage as SequenceStorage;
                if (seq != null)
                {
                    yield return ssa.Procedure.Frame.EnsureIdentifier(seq.Head);
                    yield return ssa.Procedure.Frame.EnsureIdentifier(seq.Tail);
                }
                else
                {
                    yield return id;
                }
            }
        }

        public static IEnumerable<Identifier> ResolveOverlaps(IEnumerable<Identifier> ids)
        {
            var registerBag = new Dictionary<StorageDomain, HashSet<Identifier>>();
            var others = new List<Identifier>();
            foreach (var id in ids)
            {
                if (id.Storage is RegisterStorage)
                {
                    var dom = id.Storage.Domain;
                    HashSet<Identifier> aliases;
                    if (registerBag.TryGetValue(dom, out aliases))
                    {
                        aliases.RemoveWhere(a => id.Storage.Covers(a.Storage));
                        if (!aliases.Any(a => a.Storage.Covers(id.Storage)))
                            aliases.Add(id);
                    }
                    else
                    {
                        aliases = new HashSet<Identifier> { id };
                        registerBag.Add(dom, aliases);
                    }
                }
                else
                {
                    others.Add(id);
                }
            }
            return registerBag.Values.SelectMany(s => s).Concat(others);
        }

        public IEnumerable<Identifier> ExpandFlags(IEnumerable<Identifier> ids)
        {
            var output = new List<Identifier>();
            foreach (var id in ids)
            {
                var grf = id.Storage as FlagGroupStorage;
                if (grf != null)
                {
                    output.AddRange(
                        grf.GetFlagBitMasks()
                            .Select(u => ssa.Procedure.Frame.EnsureFlagGroup(
                                 arch.GetFlagGroup(u))));
                }
                else
                {
                    output.Add(id);
                }
            }
            return output;
        }

        public override Instruction TransformAssignment(Assignment a)
        {
            if (a is AliasAssignment)
                return a;
            var src = a.Src.Accept(this);
            Identifier idNew = this.RenameFrameAccesses ? a.Dst : NewDef(a.Dst, src, false);
            return new Assignment(idNew, src);
        }

        /// <summary>
        /// Handle a call to another procedure. If the procedure has a
        /// signature, we can create an Application immediately. If the
        /// procedure has a defined ProcedureFlow, we use the BitsUsed
        /// and Trashed sets to set the uses and defs sets of the call
        /// instruction. If the called procedure is part of a recursive
        /// nest, or is a "hell node" (a hell node is an indirect call or
        /// indirect jump that prior Reko passes have been unable to resolve),
        /// we must assume the worst and use all defined registers and 
        /// trash everything. The hope is that, for recursive procedures
        /// at least, we can eliminate some of the uses and defines.
        /// </summary>
        /// <param name="ci"></param>
        /// <returns></returns>
        public override Instruction TransformCallInstruction(CallInstruction ci)
        {
            ci.Callee = ci.Callee.Accept(this);
            ProcedureBase callee = GetCalleeProcedure(ci);
            if (callee != null && callee.Signature != null && callee.Signature.ParametersValid)
            {
                var ab = CreateApplicationBuilder(ci.Callee.DataType, callee, ci);
                var instr = ab.CreateInstruction(callee.Signature, callee.Characteristics);
                return instr.Accept(this);
            }
            ProcedureFlow calleeFlow;
            var proc = callee as Procedure;
            if (proc != null && programFlow.ProcedureFlows.TryGetValue(proc, out calleeFlow))
            {
                GenerateUseDefsForKnownCallee(ci, proc, calleeFlow);
            }
            else
            {
                GenerateUseDefsForUnknownCallee(ci);
            }
            return ci;
        }

        private ApplicationBuilder CreateApplicationBuilder(DataType dt, ProcedureBase eCallee, CallInstruction call)
        {
            var pc = new ProcedureConstant(dt, eCallee);
            var ab = new FrameApplicationBuilder(arch, ssa.Procedure.Frame, call.CallSite, pc, false);
            return ab;
        }

        private void GenerateUseDefsForKnownCallee(CallInstruction ci, Procedure callee, ProcedureFlow calleeFlow)
        {
            if (this.RenameFrameAccesses)
            {
                foreach (var use in ci.Uses)
                {
                    use.Expression = use.Expression.Accept(this);
                }
            }
            else
            {
                var ab = new FrameApplicationBuilder(arch, ssa.Procedure.Frame, ci.CallSite, ci.Callee, false);
                foreach (var use in calleeFlow.BitsUsed.Keys)
                {
                    var arg = use.Accept(ab);
                    arg = arg.Accept(this);
                    ci.Uses.Add(new CallBinding(use, arg));
                }
                foreach (var def in calleeFlow.Trashed)
                {
                    var d = ssa.Procedure.Frame.EnsureIdentifier(def);
                    ci.Definitions.Add(
                        new CallBinding(
                            def,
                            NewDef(d, ci.Callee, false)));
                }
            }
        }

        private void GenerateUseDefsForUnknownCallee(CallInstruction ci)
        {
            //$TODO special case for flags; unify them all into an �berflag.
            var existingUses = ci.Uses
                .Select(u => u.Storage)
                .ToHashSet();
            var existingDefs = ci.Definitions
                .Select(d => d.Storage)
                .ToHashSet();

            // Hell node implementation - use and define all variables.
            foreach (Identifier id in ssa.Procedure.Frame.Identifiers)
            {
                if (!existingUses.Contains(id.Storage) &&
                    ((id.Storage is RegisterStorage && !(id.Storage is TemporaryStorage))
                        || id.Storage is StackStorage))
                {
                    ci.Uses.Add(new CallBinding(
                        id.Storage,
                        NewUse(id, stmCur, true)));
                    existingUses.Add(id.Storage);
                }
                if (!existingDefs.Contains(id.Storage) &&
                    ((id.Storage is RegisterStorage && !(id.Storage is TemporaryStorage))
                    || id.Storage is FlagGroupStorage))
                {
                    ci.Definitions.Add(new CallBinding(
                        id.Storage,
                        NewDef(id, ci.Callee, false)));
                    existingDefs.Add(id.Storage);
                }
            }
        }

        private ProcedureBase GetCalleeProcedure(CallInstruction ci)
        {
            Identifier id;
            ProcedureConstant pc;
            if (ci.Callee.As(out id))
            {
                pc = ssa.Identifiers[id].DefExpression as ProcedureConstant;
                if (pc == null)
                    return null;
            }
            else if (!ci.Callee.As(out pc))
            {
                return null;
            }
            return pc.Procedure;
        }

        public override Instruction TransformDefInstruction(DefInstruction def)
        {
            return def;
        }

        public override Instruction TransformStore(Store store)
        {
            store.Src = store.Src.Accept(this);
            var acc = store.Dst as MemoryAccess;
            if (acc != null)
            {
                if (this.RenameFrameAccesses && IsFrameAccess(ssa.Procedure, acc.EffectiveAddress))
                {
                    ssa.Identifiers[ssa.Procedure.Frame.FramePointer].Uses.Remove(stmCur);
                    ssa.Identifiers[acc.MemoryId].DefStatement = null;
                    var idFrame = EnsureStackVariable(ssa.Procedure, acc.EffectiveAddress, acc.DataType);
                    var idDst = NewDef(idFrame, store.Src, false);
                    return new Assignment(idDst, store.Src);
                }
                else
                {
                    SegmentedAccess sa = acc as SegmentedAccess;
                    if (sa != null)
                        sa.BasePointer = sa.BasePointer.Accept(this);
                    acc.EffectiveAddress = acc.EffectiveAddress.Accept(this);
                    if (!this.RenameFrameAccesses)
                        UpdateMemoryIdentifier(acc, true);
                }
            }
            else
            {
                store.Dst = store.Dst.Accept(this);
            }
            return store;
        }

        public override Instruction TransformUseInstruction(UseInstruction u)
        {
            if (u.OutArgument != null && !RenameFrameAccesses)
            {
                var sidOut = ssa.Identifiers.Add(u.OutArgument, null, null, false);
                sidOut.DefStatement = stmCur;
                u.OutArgument = sidOut.Identifier;
            }
            return base.TransformUseInstruction(u);
        }

        public override Expression VisitApplication(Application appl)
        {
            for (int i = 0; i < appl.Arguments.Length; ++i)
            {
                var outArg = appl.Arguments[i] as OutArgument;
                if (outArg != null)
                {
                    var id = outArg.Expression as Identifier;
                    if (id != null)
                    {
                        appl.Arguments[i] = new OutArgument(
                            outArg.DataType,
                            NewDef(id, appl, true));
                        continue;
                    }
                }
                appl.Arguments[i] = appl.Arguments[i].Accept(this);
            }
            appl.Procedure = appl.Procedure.Accept(this);
            return appl;
        }

        public override Expression VisitIdentifier(Identifier id)
        {
            return NewUse(id, stmCur, false);
        }

        public override Expression VisitOutArgument(OutArgument outArg)
        {
            var id = outArg.Expression as Identifier;
            Expression exp;
            if (id != null)
            {
                exp = RenameFrameAccesses ? id : NewDef(id, outArg, true);
            }
            else
                exp = outArg.Expression.Accept(this);
            return new OutArgument(outArg.DataType, exp);
        }

        public Identifier NewDef(Identifier idOld, Expression src, bool isSideEffect)
        {
            SsaIdentifier sidOld;
            if (idOld != null && ssa.Identifiers.TryGetValue(idOld, out sidOld))
            {
                if (sidOld.OriginalIdentifier != sidOld.Identifier)
                {
                    // Already renamed by a previous pass.
                    return sidOld.Identifier;
                }
            }
            var sid = ssa.Identifiers.Add(idOld, stmCur, src, isSideEffect);
            var bs = blockstates[block];
            var x = factory.Create(idOld, stmCur);
            return x.NewDef(bs, sid);
        }

        private Expression NewUse(Identifier id, Statement stm, bool force)
        {
            if (RenameFrameAccesses && !force)
                return id;
            var bs = blockstates[block];
            var x = factory.Create(id, stm);
            return x.NewUse(bs);
        }

        public override Expression VisitMemoryAccess(MemoryAccess access)
        {
            if (this.RenameFrameAccesses && IsFrameAccess(ssa.Procedure, access.EffectiveAddress))
            {
                ssa.Identifiers[ssa.Procedure.Frame.FramePointer].Uses.Remove(stmCur);
                var idFrame = EnsureStackVariable(ssa.Procedure, access.EffectiveAddress, access.DataType);
                var idNew = NewUse(idFrame, stmCur, true);
                return idNew;
            }

            var ea = access.EffectiveAddress.Accept(this);
            BinaryExpression bin;
            Identifier id;
            Constant c = null;
            if (ea.As(out bin) &&
                bin.Left.As(out id) &&
                bin.Right.As(out c))
            {
                var sid = ssa.Identifiers[id];
                var cOther = sid.DefExpression as Constant;
                if (cOther != null)
                {
                    c = bin.Operator.ApplyConstants(cOther, c);
                    sid.Uses.Remove(stmCur);
                }
                else
                {
                    c = null;
                }
            }
            else
            {
                c = ea as Constant;
            }

            if (c != null)
            {
                access.EffectiveAddress = c;
                var e = importResolver.ResolveToImportedProcedureConstant(stmCur, c);
                if (e != null)
                    return e;
                ea = c;
            }
            UpdateMemoryIdentifier(access, false);
            access.EffectiveAddress = ea;
            return access;
        }

        public override Expression VisitSegmentedAccess(SegmentedAccess access)
        {
            if (this.RenameFrameAccesses && IsFrameAccess(ssa.Procedure, access.EffectiveAddress))
            {
                var idFrame = EnsureStackVariable(ssa.Procedure, access.EffectiveAddress, access.DataType);
                var idNew = NewUse(idFrame, stmCur, true);
                return idNew;
            }
            else
            {
                access.BasePointer = access.BasePointer.Accept(this);
                access.EffectiveAddress = access.EffectiveAddress.Accept(this);
                access.MemoryId = (MemoryIdentifier)NewUse(access.MemoryId, stmCur, false);
                return access;
            }
        }

        private void UpdateMemoryIdentifier(MemoryAccess access, bool storing)
        {
            if (storing)
            {
                var sid = ssa.Identifiers.Add(access.MemoryId, this.stmCur, null, false);
                var ss = new SsaRegisterTransformer(access.MemoryId, stmCur, this);
                access.MemoryId = (MemoryIdentifier)ss.WriteVariable(blockstates[block], sid, false);
            }
            else
            {
                access.MemoryId = (MemoryIdentifier)access.MemoryId.Accept(this);
            }
        }

        private static bool IsFrameAccess(Procedure proc, Expression e)
        {
            if (e == proc.Frame.FramePointer)
                return true;
            var bin = e as BinaryExpression;
            if (bin == null)
                return false;
            if (bin.Left != proc.Frame.FramePointer)
                return false;
            return bin.Right is Constant;
        }

        private static Identifier EnsureStackVariable(Procedure proc, Expression effectiveAddress, DataType dt)
        {
            if (effectiveAddress == proc.Frame.FramePointer)
                return proc.Frame.EnsureStackVariable(0, dt);
            var bin = (BinaryExpression)effectiveAddress;
            var offset = ((Constant)bin.Right).ToInt32();
            if (bin.Operator == Operator.ISub)
                offset = -offset;
            var idFrame = proc.Frame.EnsureStackVariable(offset, dt);
            return idFrame;
        }

        private void ProcessIncompletePhis()
        {
            foreach (var phi in incompletePhis)
            {
                var phiBlock = phi.DefStatement.Block;
                var x = factory.Create(phi.OriginalIdentifier, phi.DefStatement);
                x.AddPhiOperandsCore(phi, true);
            }
            incompletePhis.Clear();
        }

        public class SsaBlockState
        {
            public readonly Block Block;
            public readonly Dictionary<StorageDomain, AliasState> currentDef;
            public readonly Dictionary<uint, SsaIdentifier> currentFlagDef;
            public readonly Dictionary<int, SsaIdentifier> currentStackDef;
            public readonly Dictionary<int, SsaIdentifier> currentFpuDef;
            public bool Visited;

            public SsaBlockState(Block block)
            {
                this.Block = block;
                this.Visited = false;
                this.currentDef = new Dictionary<StorageDomain, AliasState>();
                this.currentFlagDef = new Dictionary<uint, SsaIdentifier>();
                this.currentStackDef = new Dictionary<int, SsaIdentifier>();
                this.currentFpuDef = new Dictionary<int, SsaIdentifier>();
            }

#if DEBUG
            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendFormat("BlockState {0}", Block.Name);
                sb.AppendLine();
                sb.AppendFormat("    {0}",
                    string.Join(",", currentDef.Keys.Select(k => ((int)k).ToString())));
                return sb.ToString();
            }
#endif
        }

        public class AliasState
        {
            public SsaIdentifier SsaId;        // The id that actually was modified.
            public readonly IDictionary<Identifier, SsaIdentifier> Aliases;     // Other ids that were affected by this stm.
            public AliasState(SsaIdentifier ssaId)
            {
                this.SsaId = ssaId;
                this.Aliases = new Dictionary<Identifier, SsaIdentifier>();
            }

#if DEBUG
            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.AppendFormat("Alias: {0}", SsaId.Identifier.Name);
                if (Aliases.Count > 0)
                {
                    sb.AppendFormat(" = {0}", string.Join(", ", Aliases.Values.OrderBy(v => v.Identifier.Name)));
                }
                return sb.ToString();
            }
#endif
        }

        public class TransformerFactory : StorageVisitor<SsaIdentifierTransformer>
        {
            private SsaTransform transform;
            private Identifier id;
            private Statement stm;

            public TransformerFactory(SsaTransform transform)
            {
                this.transform = transform;
            }

            public SsaIdentifierTransformer Create(Identifier id, Statement stm)
            {
                this.id = id;
                this.stm = stm;
                return id.Storage.Accept(this);
            }

            public SsaIdentifierTransformer VisitFlagGroupStorage(FlagGroupStorage grf)
            {
                return new FlagGroupTransformer(id, grf, stm, transform);
            }

            public SsaIdentifierTransformer VisitFlagRegister(FlagRegister freg)
            {
                return new SsaRegisterTransformer(id, stm, transform);
            }

            public SsaIdentifierTransformer VisitFpuStackStorage(FpuStackStorage fpu)
            {
                return new FpuStackTransformer(id, fpu, stm, transform);
            }

            public SsaIdentifierTransformer VisitMemoryStorage(MemoryStorage global)
            {
                return new SsaRegisterTransformer(id, stm, transform);
            }

            public SsaIdentifierTransformer VisitOutArgumentStorage(OutArgumentStorage arg)
            {
                throw new NotImplementedException();
            }

            public SsaIdentifierTransformer VisitRegisterStorage(RegisterStorage reg)
            {
                return new SsaRegisterTransformer(id, stm, transform);
            }

            public SsaIdentifierTransformer VisitSequenceStorage(SequenceStorage seq)
            {
                return new SsaSequenceTransformer(id, seq, stm, transform);
            }

            public SsaIdentifierTransformer VisitStackArgumentStorage(StackArgumentStorage stack)
            {
                return new SsaStackTransformer(id, stack.StackOffset, stm, transform);
            }

            public SsaIdentifierTransformer VisitStackLocalStorage(StackLocalStorage local)
            {
                return new SsaStackTransformer(id, local.StackOffset, stm, transform);
            }

            public SsaIdentifierTransformer VisitTemporaryStorage(TemporaryStorage temp)
            {
                return new SsaRegisterTransformer(id, stm, transform);
            }
        }

        public abstract class SsaIdentifierTransformer
        {
            protected Identifier id;
            protected readonly Statement stm;
            protected readonly SsaTransform outer;
            protected readonly SsaIdentifierCollection ssaIds;
            protected readonly IDictionary<Block, SsaBlockState> blockstates;

            public SsaIdentifierTransformer(Identifier id, Statement stm, SsaTransform outer)
            {
                this.id = id;
                this.stm = stm;
                this.ssaIds = outer.ssa.Identifiers;
                this.blockstates = outer.blockstates;
                this.outer = outer;
            }

            public virtual Expression NewUse(SsaBlockState bs)
            {
                var sid = ReadVariable(bs, true);
                sid.Uses.Add(stm);
                return sid.Identifier;
            }

            public virtual Identifier NewDef(SsaBlockState bs, SsaIdentifier sid)
            {
                return WriteVariable(bs, sid, true);
            }

            /// <summary>
            /// Registers the fact that identifier <paramref name="id"/> is
            /// modified in the block <paramref name="b" />. 
            /// </summary>
            /// <param name="bs">The block in which the identifier was changed</param>
            /// <param name="sid">The identifier after being SSA transformed.</param>
            /// <param name="performProbe">if true, looks "backwards" to see
            ///   if <paramref name="id"/> overlaps with another identifier</param>
            /// <returns></returns>
            public virtual Identifier WriteVariable(SsaBlockState bs, SsaIdentifier sid, bool performProbe)
            {
                if (performProbe)
                {
                    // Did a previous SSA id modify storage that overlaps id?
                    if (ProbeVariable(bs))
                    {
                        var id = this.id;
                        var sidPrev = ReadVariable(bs, false);

                        // Generate a DPB so the previous modification "shines
                        // through".
                        var sidPrevOld = sidPrev;
                        var dpb = new DepositBits(sidPrev.Identifier, sid.Identifier, (int)id.Storage.BitAddress);
                        var ass = new AliasAssignment(sidPrev.OriginalIdentifier, dpb);
                        sidPrev = InsertAfterDefinition(sid.DefStatement, ass);
                        sidPrevOld.Uses.Add(sidPrev.DefStatement);

                        var alias = new AliasState(sidPrev);
                        alias.Aliases.Add(id, sid);
                        //Debug.Print("--- {0}: {1}", bs.Block.Name, sid.Identifier.Name);
                        bs.currentDef[id.Storage.Domain] = alias;
                        return sid.Identifier;
                    }
                }
                //if (!(sid.DefStatement.Instruction is AliasAssignment))
                {
                    //Debug.Print("--- {0}: {1}", bs.Block.Name, sid.Identifier.Name);
                    bs.currentDef[id.Storage.Domain] = new AliasState(sid);
                }
                return sid.Identifier;
            }

            /// <summary>
            /// Reaches "backwards" to locate the SSA identifier that defines
            /// the identifier <paramref name="id"/>, starting in block <paramref name="b"/>.
            /// </summary>
            /// If no definition of <paramref name="id"/> is found, a new 
            /// DefStatement is created in the entry block of the procedure,
            /// </summary>
            /// <param name="id"></param>
            /// <param name="b"></param>
            /// <param name="aliasProbe"></param>
            /// <returns></returns>
            public virtual SsaIdentifier ReadVariable(SsaBlockState bs, bool generateAlias)
            {
                var sid = ReadBlockLocalVariable(bs, generateAlias);
                if (sid != null)
                    return sid;
                // Keep probin'.
                return ReadVariableRecursive(bs, generateAlias);
            }

            public bool ProbeVariable(SsaBlockState bs)
            {
                if (ProbeBlockLocalVariable(bs))
                    return true;
                return ProbeVariable(bs, new HashSet<Block>());
            }

            public bool ProbeVariable(SsaBlockState bs, HashSet<Block> visited)
            {
                if (bs.Block.Pred.Any(p => !blockstates[p].Visited))
                    return false;
                foreach (var p in bs.Block.Pred)
                {
                    if (visited.Contains(p))
                        continue;
                    if (ProbeBlockLocalVariable(this.blockstates[p]))
                        return true;
                }
                return false;
            }

            public abstract SsaIdentifier ReadBlockLocalVariable(SsaBlockState bs, bool generateAlias);
            public abstract bool ProbeBlockLocalVariable(SsaBlockState bs);

            public SsaIdentifier ReadVariableRecursive(SsaBlockState bs, bool generateAlias)
            {
                SsaIdentifier val;
                if (false)  // !sealedBlocks.Contains(b))
                {
                    // Incomplete CFG
                    //val = newPhi(id, b);
                    //incompletePhis[b][id.Storage] = val;
                }
                else if (bs.Block.Pred.Count == 0)
                {
                    // Undef'ined or unreachable parameter; assume it's a def.
                    val = NewDefInstruction(id, bs.Block);
                }
                else if (bs.Block.Pred.Count == 1)
                {
                    val = ReadVariable(blockstates[bs.Block.Pred[0]], generateAlias);
                }
                else
                {
                    // Break potential cycles with operandless phi
                    val = NewPhi(id, bs.Block);
                    WriteVariable(bs, val, false);
                    val = AddPhiOperands(val, generateAlias);
                }
                if (val != null)
                    WriteVariable(bs, val, false);
                return val;
            }

            /// <summary>
            /// If <paramref name="idTo"/> is smaller than <paramref name="sidFrom" />, then
            /// it doesn't cover it completely. Therefore, we must generate a SLICE / cast 
            /// statement.
            /// </summary>
            /// <param name="idTo"></param>
            /// <param name="sidFrom"></param>
            /// <returns></returns>
            protected SsaIdentifier MaybeGenerateAliasStatement(AliasState alias)
            {
                var b = alias.SsaId.DefStatement.Block;
                var sidFrom = alias.SsaId;
                var stgTo = id.Storage;
                Storage stgFrom = sidFrom.Identifier.Storage;
                if (stgFrom == stgTo)
                {
                    alias.Aliases[id] = sidFrom;
                    return alias.SsaId;
                }

                Expression e = null;
                SsaIdentifier sidUse;
                if (stgFrom.Covers(stgTo))
                {
                    int offset = stgFrom.OffsetOf(stgTo);
                    if (offset > 0)
                        e = new Slice(id.DataType, sidFrom.Identifier, (uint)offset);
                    else
                        e = new Cast(id.DataType, sidFrom.Identifier);
                    sidUse = alias.SsaId;
                }
                else
                {
                    sidUse = ReadVariableRecursive(blockstates[alias.SsaId.DefStatement.Block], true);
                    e = new DepositBits(sidUse.Identifier, alias.SsaId.Identifier, (int)stgFrom.BitAddress);
                }
                var ass = new AliasAssignment(id, e);
                var sidAlias = InsertAfterDefinition(sidFrom.DefStatement, ass);
                sidUse.Uses.Add(sidAlias.DefStatement);
                if (e is DepositBits)
                    sidFrom.Uses.Add(sidAlias.DefStatement);
                alias.Aliases[id] = sidAlias;
                return sidAlias;
            }

            /// <summary>
            /// Inserts the statement <paramref name="ass"/> after the statement
            /// <paramref name="stmBefore"/>, skipping any AliasAssignments that
            /// statements that may have been added after 
            /// <paramref name="stmBefore"/>.
            /// </summary>
            /// <param name="stmBefore"></param>
            /// <param name="ass"></param>
            /// <returns></returns>
            public SsaIdentifier InsertAfterDefinition(Statement stmBefore, AliasAssignment ass)
            {
                var b = stmBefore.Block;
                int i = b.Statements.IndexOf(stmBefore);
                // Skip alias statements
                while (i < b.Statements.Count - 1 && b.Statements[i + 1].Instruction is AliasAssignment)
                    ++i;
                var stm = new Statement(stmBefore.LinearAddress, ass, stmBefore.Block);
                stmBefore.Block.Statements.Insert(i + 1, stm);

                var sidTo = ssaIds.Add(ass.Dst, stm, ass.Src, false);
                ass.Dst = sidTo.Identifier;
                return sidTo;
            }

            /// <summary>
            /// Creates a phi statement with no slots for the predecessor blocks, then
            /// inserts the phi statement as the first statement of the block.
            /// </summary>
            /// <param name="b">Block into which the phi statement is inserted</param>
            /// <param name="v">Destination variable for the phi assignment</param>
            /// <returns>The inserted phi Assignment</returns>
            private SsaIdentifier NewPhi(Identifier id, Block b)
            {
                var phiAss = new PhiAssignment(id, 0);
                var stm = new Statement(0, phiAss, b);
                b.Statements.Insert(0, stm);
                var sid = ssaIds.Add(phiAss.Dst, stm, phiAss.Src, false);
                phiAss.Dst = sid.Identifier;
                return sid;
            }

            private SsaIdentifier AddPhiOperands(SsaIdentifier phi, bool generateAlias)
            {
                // Determine operands from predecessors.
                var preds = phi.DefStatement.Block.Pred;

                if (preds.Any(p => !blockstates[p].Visited))
                {
                    // Haven't visited some of the predecessors yet,
                    // so we can't backwalk... yet. 
                    ((PhiAssignment)phi.DefStatement.Instruction).Src =
                                new PhiFunction(phi.Identifier.DataType, new Expression[preds.Count]);
                    outer.incompletePhis.Add(phi);
                    return phi;
                }
                return AddPhiOperandsCore(phi, generateAlias);
            }

            public SsaIdentifier AddPhiOperandsCore(SsaIdentifier phi, bool generateAlias)
            {
                var preds = phi.DefStatement.Block.Pred;
                var sids = preds.Select(p => ReadVariable(blockstates[p], generateAlias)).ToArray();
                GeneratePhiFunction(phi, sids);

                return TryRemoveTrivial(phi);
            }

            private static void GeneratePhiFunction(SsaIdentifier phi, SsaIdentifier[] sids)
            {
                ((PhiAssignment)phi.DefStatement.Instruction).Src =
                new PhiFunction(
                        phi.Identifier.DataType,
                        sids.Select(s => s.Identifier).ToArray());
            }

            /// <summary>
            /// If the phi function is trivial, remove it.
            /// </summary>
            /// <param name="phi"></param>
            /// <returns></returns>
            private SsaIdentifier TryRemoveTrivial(SsaIdentifier phi)
            {
                Identifier same = null;
                var phiFunc = ((PhiAssignment)phi.DefStatement.Instruction).Src;
                foreach (Identifier op in phiFunc.Arguments)
                {
                    if (op == same || op == phi.Identifier)
                        continue;
                    if (same != null)
                    {
                        // A real phi; use all its arguments.
                        UsePhiArguments(phi, phiFunc);
                        return phi;
                    }
                    same = op;
                }
                SsaIdentifier sid;
                if (same == null)
                {
                    // Undef'ined or unreachable parameter; assume it's a def.
                    sid = NewDefInstruction(phi.OriginalIdentifier, phi.DefStatement.Block);
                }
                else
                {
                    sid = ssaIds[same];
                }

                // Remember all users except for phi
                var users = phi.Uses.Where(u => u != phi.DefStatement).ToList();

                // Reroute all uses of phi to use same. Remove phi.
                ReplaceBy(phi, same);

                // Remove all phi uses which may have become trivial now.
                foreach (var use in users)
                {
                    var phiAss = use.Instruction as PhiAssignment;
                    if (phiAss != null)
                    {
                        TryRemoveTrivial(ssaIds[phiAss.Dst]);
                    }
                }
                AliasState alias;
                if (blockstates[phi.DefStatement.Block].currentDef.TryGetValue(same.Storage.Domain, out alias))
                {
                    alias.SsaId = outer.ssa.Identifiers[same];
                }
                phi.DefStatement.Block.Statements.Remove(phi.DefStatement);
                this.outer.sidsToRemove.Add(phi);
                return sid;
            }

            private void UsePhiArguments(SsaIdentifier phi, PhiFunction phiFunc)
            {
                foreach (Identifier id in phiFunc.Arguments)
                {
                    ssaIds[id].Uses.Add(phi.DefStatement);
                }
            }

            private SsaIdentifier NewDefInstruction(Identifier id, Block b)
            {
                var sid = ssaIds.Add(id, null, null, false);
                sid.DefStatement = new Statement(0, new DefInstruction(id), b);
                b.Statements.Add(sid.DefStatement);
                return sid;
            }

            private void ReplaceBy(SsaIdentifier sidOld, Identifier idNew)
            {
                foreach (var use in sidOld.Uses.ToList())
                {
                    use.Instruction.Accept(new IdentifierReplacer(this.ssaIds, use, sidOld.Identifier, idNew));
                }
            }
        }

        public class SsaRegisterTransformer : SsaIdentifierTransformer
        {
            public SsaRegisterTransformer(Identifier id, Statement stm, SsaTransform outer)
                : base(id, stm, outer)
            {
            }

            public override SsaIdentifier ReadBlockLocalVariable(SsaBlockState bs, bool generateAlias)
            {
                AliasState alias;
                if (!bs.currentDef.TryGetValue(id.Storage.Domain, out alias))
                    return null;

                // Defined locally in this block.
                // Has the alias already been calculated?
                SsaIdentifier ssaId = alias.SsaId;
                if (alias.SsaId.OriginalIdentifier == id ||
                    alias.Aliases.TryGetValue(id, out ssaId))
                {
                    return ssaId;
                }

                // Does ssaId intersect the probed value?
                if (alias.SsaId.Identifier.Storage.OverlapsWith(id.Storage))
                {
                    if (generateAlias)
                    {
                        var sid = MaybeGenerateAliasStatement(alias);
                        bs.currentDef[id.Storage.Domain] = alias;
                        return sid;
                    }
                    else
                        return alias.SsaId;
                }
                return null;
            }

            public override bool ProbeBlockLocalVariable(SsaBlockState bs)
            {
                AliasState alias;
                if (bs.currentDef.TryGetValue(id.Storage.Domain, out alias))
                {
                    return 
                        alias.SsaId.Identifier.Storage.OverlapsWith(id.Storage) &&
                        alias.SsaId.Identifier.Storage.Exceeds(id.Storage);
                }
                else
                {
                    return false;
                }
            }
        }

        public class FlagGroupTransformer : SsaIdentifierTransformer
        {
            private uint flagMask;
            private FlagGroupStorage flagGroup;

            public FlagGroupTransformer(Identifier id, FlagGroupStorage flagGroup, Statement stm, SsaTransform outer)
                : base(id, stm, outer)
            {
                this.flagGroup = flagGroup;
            }

            public override Expression NewUse(SsaBlockState bs)
            {
                // Analyze each flag in the flag group separately.
                var ids = new Dictionary<Identifier, SsaIdentifier>();
                foreach (uint flagBitMask in flagGroup.GetFlagBitMasks())
                {
                    this.flagMask = flagBitMask;
                    this.id = outer.ssa.Procedure.Frame.EnsureFlagGroup(outer.arch.GetFlagGroup(flagMask));
                    var sid = ReadVariable(bs, false);
                    ids[sid.Identifier] = sid;
                }
                if (ids.Count == 1)
                {
                    var de = ids.First();
                    de.Value.Uses.Add(stm);
                    return de.Key;
                }
                else
                {
                    return OrTogether(ids.Values, stm);
                }
            }

            private Expression OrTogether(IEnumerable<SsaIdentifier> sids, Statement stm)
            {
                Expression e = null;
                foreach (var sid in sids.OrderBy(id => id.Identifier.Name))
                {
                    sid.Uses.Add(stm);
                    if (e == null)
                        e = sid.Identifier;
                    else
                        e = new BinaryExpression(Operator.Or, PrimitiveType.Byte, e, sid.Identifier);
                }
                return e;
            }

            public override Identifier WriteVariable(SsaBlockState bs, SsaIdentifier sid, bool performProbe)
            {
                bs.currentFlagDef[flagMask] = sid;
                return sid.Identifier;
            }

            public override SsaIdentifier ReadBlockLocalVariable(SsaBlockState bs, bool generateAlias)
            {
                SsaIdentifier ssaId;
                if (!bs.currentFlagDef.TryGetValue(flagMask, out ssaId))
                    return null;
                
                // Defined locally in this block.
                return ssaId;
            }

            public override bool ProbeBlockLocalVariable(SsaBlockState bs)
            {
                // Bits don't have substructure.
                return false;
            }

            public override Identifier NewDef(SsaBlockState bs, SsaIdentifier sid)
            {
                foreach (uint flagBitMask in flagGroup.GetFlagBitMasks())
                {
                    this.flagMask = flagBitMask;
                    WriteVariable(bs, sid, true);
                }
                return sid.Identifier;
            }
        }

        public class SsaStackTransformer : SsaIdentifierTransformer
        {
            private int stackOffset;

            public SsaStackTransformer(
                Identifier id,
                int stackOffset,
                Statement stm,
                SsaTransform outer)
                : base(id, stm, outer)
            {
                this.stackOffset = stackOffset;
            }

            public override Expression NewUse(SsaBlockState bs)
            {
                var sid = ReadVariable(bs, false);
                sid.Uses.Add(stm);
                return sid.Identifier;
            }

            public override Identifier NewDef(SsaBlockState bs, SsaIdentifier sid)
            {
                return WriteVariable(bs, sid, true);
            }

            public override SsaIdentifier ReadBlockLocalVariable(SsaBlockState bs, bool generateAlias)
            {
                SsaIdentifier ssaId;
                if (bs.currentStackDef.TryGetValue(stackOffset, out ssaId))
                {
                    // Defined locally in this block.
                    return ssaId;
                }
                return null;
            }

            public override bool ProbeBlockLocalVariable(SsaBlockState bs)
            {
                return bs.currentStackDef.ContainsKey(stackOffset);
            }

            public override Identifier WriteVariable(SsaBlockState bs, SsaIdentifier sid, bool performProbe)
            {
                bs.currentStackDef[stackOffset] = sid;
                return sid.Identifier;
            }
        }

        public class SsaSequenceTransformer : SsaIdentifierTransformer
        {
            private SequenceStorage seq;

            public SsaSequenceTransformer(
                Identifier id,
                SequenceStorage seq,
                Statement stm,
                SsaTransform outer)
                : base(id, stm, outer)
            {
                this.seq = seq;
            }

            public override SsaIdentifier ReadVariable(SsaBlockState bs, bool generateAlias)
            {
                var ss = outer.factory.Create(outer.ssa.Procedure.Frame.EnsureIdentifier(seq.Head), stm);
                var head = ss.ReadVariable(bs, generateAlias);
                ss = outer.factory.Create(outer.ssa.Procedure.Frame.EnsureIdentifier(seq.Tail), stm);
                var tail = ss.ReadVariable(bs, generateAlias);
                return Fuse(head, tail);
            }

            public override SsaIdentifier ReadBlockLocalVariable(SsaBlockState bs, bool generateAlias)
            {
                // We shouldn't reach this, as ReadVariable above should have 
                // broken the sequence into a head and tail read.
                throw new InvalidOperationException();
            }

            public override bool ProbeBlockLocalVariable(SsaBlockState bs)
            {
                var hd = outer.factory.Create(outer.ssa.Procedure.Frame.EnsureIdentifier(seq.Head), stm);
                var tl = outer.factory.Create(outer.ssa.Procedure.Frame.EnsureIdentifier(seq.Tail), stm);
                return
                    hd.ProbeBlockLocalVariable(bs) &&
                    tl.ProbeBlockLocalVariable(bs);
            }

            public SsaIdentifier Fuse(SsaIdentifier head, SsaIdentifier tail)
            {
                AliasAssignment aassHead, aassTail;
                if (head.DefStatement.Instruction.As(out aassHead) &&
                    tail.DefStatement.Instruction.As(out aassTail))
                {
                    // 
                    Slice eHead;
                    Cast eTail;
                    if (aassHead.Src.As(out eHead) && aassTail.Src.As(out eTail))
                    {
                        return ssaIds[(Identifier)eHead.Expression];
                    }
                }
                DefInstruction defHead, defTail;
                if (head.DefStatement.Instruction.As(out defHead) &&
                    tail.DefStatement.Instruction.As(out defTail))
                {
                    // All subregisters came in from caller, so create an
                    // alias statement.
                    var seq = new MkSequence(this.id.DataType, head.Identifier, tail.Identifier);
                    var ass = new AliasAssignment(id, seq);
                    var stm = head.DefStatement.Block.Statements.Add(0, ass);
                    var sidTo = ssaIds.Add(ass.Dst, stm, ass.Src, false);
                    ass.Dst = sidTo.Identifier;
                    head.Uses.Add(stm);
                    tail.Uses.Add(stm);
                    return sidTo;
                }

                Assignment assHead, assTail;
                if (head.DefStatement.Instruction.As(out assHead) &&
                    tail.DefStatement.Instruction.As(out assTail))
                {
                    Identifier id;
                    // If x_2 = Slice(y_3); z_4 = (cast) y_3 return y_3
                    var slHead = assHead.Src as Slice;
                    var caTail = assTail.Src as Cast;
                    if (slHead != null && caTail != null &&
                        slHead.Expression == caTail.Expression &&
                        slHead.Expression.As(out id))
                    {
                        return ssaIds[id];
                    }
                }

                throw new NotImplementedException(string.Format(
                    "Can't fuse {0} and {1}.",
                    head,
                    tail));
            }

            public override Identifier WriteVariable(SsaBlockState bs, SsaIdentifier sid, bool performProbe)
            {
                var ss = outer.factory.Create(outer.ssa.Procedure.Frame.EnsureIdentifier(seq.Head), stm);
                ss.WriteVariable(bs, sid, performProbe);
                ss = outer.factory.Create(outer.ssa.Procedure.Frame.EnsureIdentifier(seq.Tail), stm);
                ss.WriteVariable(bs, sid, performProbe);
                return sid.Identifier;
            }
        }

        public class FpuStackTransformer : SsaIdentifierTransformer
        {
            private FpuStackStorage fpu;

            public FpuStackTransformer(Identifier id, FpuStackStorage fpu, Statement stm, SsaTransform outer) : base(id, stm, outer)
            {
                this.fpu = fpu;
            }

            public override Identifier NewDef(SsaBlockState bs, SsaIdentifier sid)
            {
                bs.currentFpuDef[fpu.FpuStackOffset] = sid;
                return base.NewDef(bs, sid);
            }

            public override SsaIdentifier ReadBlockLocalVariable(SsaBlockState bs, bool generateAlias)
            {
                SsaIdentifier sid;
                bs.currentFpuDef.TryGetValue(fpu.FpuStackOffset, out sid);
                return sid;
            }

            public override bool ProbeBlockLocalVariable(SsaBlockState bs)
            {
                return false;
            }
        }


    }
}
