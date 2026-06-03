using Xunit;

// The integration tests stand up real Ax25Listener pumps, TCP listeners, and
// console loops on background tasks against TimeProvider.System. Running them in
// parallel with each other (and with the WebApplicationFactory host-boot tests)
// multiplies the number of live pumps and shared OS resources and makes failures
// harder to attribute. Disable assembly parallelisation so the suite runs
// sequentially and deterministically; the unit/property tests are fast enough
// that this costs little.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
