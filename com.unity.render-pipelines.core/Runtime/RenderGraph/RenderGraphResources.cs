using System;
using System.Diagnostics;
using UnityEngine.Rendering;

namespace UnityEngine.Experimental.Rendering.RenderGraphModule
{
    // RendererList is a different case so not represented here.
    internal enum RenderGraphResourceType
    {
        Texture = 0,
        ComputeBuffer,
        Count
    }

    internal struct ResourceHandle
    {
        // Note on handles validity.
        // PassData classes used during render graph passes are pooled and because of that, when users don't fill them completely,
        // they can contain stale handles from a previous render graph execution that could still be considered valid if we only checked the index.
        // In order to avoid using those, we incorporate the execution index in a 16 bits hash to make sure the handle is coming from the current execution.
        // If not, it's considered invalid.
        // We store this validity mask in the upper 16 bits of the index.
        const uint kValidityMask = 0xFFFF0000;
        const uint kIndexMask = 0xFFFF;

        uint m_Value;

        static uint s_CurrentValidBit = 1 << 16;
        static uint s_SharedResourceValidBit = 0x7FFF << 16;

        public int index { get { return (int)(m_Value & kIndexMask); } }
        public RenderGraphResourceType type { get; private set; }
        public int iType { get { return (int)type; } }

        internal ResourceHandle(int value, RenderGraphResourceType type, bool shared)
        {
            Debug.Assert(value <= 0xFFFF);
            m_Value = ((uint)value & kIndexMask) | (shared ? s_SharedResourceValidBit : s_CurrentValidBit);
            this.type = type;
        }

        public static implicit operator int(ResourceHandle handle) => handle.index;
        public bool IsValid()
        {
            var validity = m_Value & kValidityMask;
            return validity != 0 && (validity == s_CurrentValidBit || validity == s_SharedResourceValidBit);
        }

        static public void NewFrame(int executionIndex)
        {
            // Scramble frame count to avoid collision when wrapping around.
            s_CurrentValidBit = (uint)(((executionIndex >> 16) ^ (executionIndex & 0xffff) * 58546883) << 16);
            // In case the current valid bit is 0, even though perfectly valid, 0 represents an invalid handle, hence we'll
            // trigger an invalid state incorrectly. To account for this, we actually skip 0 as a viable s_CurrentValidBit and
            // start from 1 again.
            if (s_CurrentValidBit == 0)
            {
                s_CurrentValidBit = 1 << 16;
            }
        }
    }

    class IRenderGraphResource
    {
        public bool imported;
        public bool shared;
        public int cachedHash;
        public int transientPassIndex;
        public int sharedResourceLastFrameUsed;

        protected IRenderGraphResourcePool m_Pool;

        public virtual void Reset(IRenderGraphResourcePool pool)
        {
            imported = false;
            shared = false;
            cachedHash = -1;
            transientPassIndex = -1;
            sharedResourceLastFrameUsed = -1;

            m_Pool = pool;
        }

        public virtual string GetName()
        {
            return "";
        }

        public virtual bool IsCreated()
        {
            return false;
        }

        public virtual void CreatePooledGraphicsResource() { }
        public virtual void CreateGraphicsResource(string name = "") { }
        public virtual void ReleasePooledGraphicsResource(int frameIndex) { }
        public virtual void ReleaseGraphicsResource() { }
        public virtual void LogCreation(RenderGraphLogger logger) { }
        public virtual void LogRelease(RenderGraphLogger logger) { }
    }

    [DebuggerDisplay("Resource ({GetType().Name}:{GetName()})")]
    abstract class RenderGraphResource<DescType, ResType>
        : IRenderGraphResource
        where DescType : struct
        where ResType : class
    {
        public DescType desc;
        public ResType graphicsResource;

        protected RenderGraphResource()
        {
        }

        public override void Reset(IRenderGraphResourcePool pool)
        {
            base.Reset(pool);
            graphicsResource = null;
        }

        public override bool IsCreated()
        {
            return graphicsResource != null;
        }

        public override void CreatePooledGraphicsResource()
        {
            Debug.Assert(m_Pool != null, "CreatePooledGraphicsResource should only be called for regular pooled resources");

            int hashCode = desc.GetHashCode();

            if (graphicsResource != null)
                throw new InvalidOperationException(string.Format("Trying to create an already created resource ({0}). Resource was probably declared for writing more than once in the same pass.", GetName()));

            var pool = m_Pool as RenderGraphResourcePool<ResType>;
            if (!pool.TryGetResource(hashCode, out graphicsResource))
            {
                CreateGraphicsResource();
            }

            cachedHash = hashCode;
            pool.RegisterFrameAllocation(cachedHash, graphicsResource);
        }

        public override void ReleasePooledGraphicsResource(int frameIndex)
        {
            if (graphicsResource == null)
                throw new InvalidOperationException($"Tried to release a resource ({GetName()}) that was never created. Check that there is at least one pass writing to it first.");

            // Shared resources don't use the pool
            var pool = m_Pool as RenderGraphResourcePool<ResType>;
            if (pool != null)
            {
                pool.ReleaseResource(cachedHash, graphicsResource, frameIndex);
                pool.UnregisterFrameAllocation(cachedHash, graphicsResource);
            }

            Reset(null);
        }

        public override void ReleaseGraphicsResource()
        {
            base.ReleaseGraphicsResource();

            Reset(null);
        }
    }
}
