export function testOutputDirectory(environment = process.env) {
  const directory = environment.PORTFOLIO_TEST_OUT_DIR ?? "dist";
  if (directory !== "dist" && directory !== ".root-build") {
    throw new Error("PORTFOLIO_TEST_OUT_DIR must be dist or .root-build.");
  }
  return directory;
}
