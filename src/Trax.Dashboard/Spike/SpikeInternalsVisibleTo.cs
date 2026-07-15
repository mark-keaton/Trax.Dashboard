// SPIKE-ONLY: lets the throwaway render host under scratchpad/spikehost drive the internal spike
// endpoints without widening Trax.Dashboard's public API surface (guarded by PublicApiSurfaceTests).
// Delete this file together with the Spike/ folder when the spike is retired.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("spikehost")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("traxhost")]
