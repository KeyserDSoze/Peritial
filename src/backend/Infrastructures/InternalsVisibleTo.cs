using System.Runtime.CompilerServices;

// Keep the production recovery byte-budget helper internal while letting
// the test assembly count exact bytes read from adversarial streams.
[assembly: InternalsVisibleTo("Peritial.Infrastructures.Tests")]
