// The tests share one site: the write tests make and delete scratch content that the oracle would otherwise sample
// halfway through.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
