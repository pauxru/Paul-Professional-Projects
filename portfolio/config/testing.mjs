export function testOutputDirectory(environment = process.env) {
  const directory = environment.PORTFOLIO_TEST_OUT_DIR ?? "dist";
  if (!["dist", ".root-build", ".repo-build"].includes(directory)) {
    throw new Error("PORTFOLIO_TEST_OUT_DIR must be dist, .root-build or .repo-build.");
  }
  return directory;
}
