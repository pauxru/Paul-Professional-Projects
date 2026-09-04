cmake_minimum_required(VERSION 3.20)
project(contoso_pricing CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# All three DLLs land next to each other so the .NET host can load them by name from a
# single probing directory. Without this, MSVC scatters them by configuration.
foreach(cfg "" _DEBUG _RELEASE _RELWITHDEBINFO _MINSIZEREL)
  set(CMAKE_RUNTIME_OUTPUT_DIRECTORY${cfg} ${CMAKE_BINARY_DIR}/bin)
  set(CMAKE_LIBRARY_OUTPUT_DIRECTORY${cfg} ${CMAKE_BINARY_DIR}/bin)
endforeach()

if(MSVC)
  add_compile_options(/W4 /permissive- /EHsc)
  # Warnings are errors. A boundary that compiles with warnings is a boundary nobody
  # has read.
  add_compile_options(/WX)
endif()

set(PJ_SOURCES src/engine.cpp src/abi.cpp)

# ---------------------------------------------------------------------------------
# Three builds of the same source. The only differences are the two knobs below, which
# is what lets docs/results.md attribute every difference it measures.
# ---------------------------------------------------------------------------------

# 1. pricing.dll -- hardened boundary, strict IEEE-754. This is what ships.
add_library(pricing SHARED ${PJ_SOURCES})
target_include_directories(pricing PUBLIC include)
target_compile_definitions(pricing PRIVATE PJ_HARDENED=1)
if(MSVC)
  target_compile_options(pricing PRIVATE /fp:precise /O2)
endif()

# 2. pricing_legacy.dll -- the boundary exactly as it was found in 2009: no argument
#    validation, no capacity checks, no exception barrier. Identical maths. Exists so
#    the fuzz differential has something to differ from. NEVER SHIPPED.
add_library(pricing_legacy SHARED ${PJ_SOURCES})
target_include_directories(pricing_legacy PUBLIC include)
target_compile_definitions(pricing_legacy PRIVATE PJ_HARDENED=0)
if(MSVC)
  target_compile_options(pricing_legacy PRIVATE /fp:precise /O2)
  # The 2009 build used strcpy without complaint; reproducing the boundary means
  # reproducing the build that allowed it.
  target_compile_definitions(pricing_legacy PRIVATE _CRT_SECURE_NO_WARNINGS)
  target_compile_options(pricing_legacy PRIVATE /wd4996)
endif()

# 3. pricing_fast.dll -- hardened boundary, but recompiled the way a modernisation
#    programme is tempted to recompile it: relaxed floating point and a newer
#    instruction set. Same engine.cpp, same validation. The report measures whether
#    that changes the number the business books.
add_library(pricing_fast SHARED ${PJ_SOURCES})
target_include_directories(pricing_fast PUBLIC include)
target_compile_definitions(pricing_fast PRIVATE PJ_HARDENED=1)
if(MSVC)
  target_compile_options(pricing_fast PRIVATE /fp:fast /arch:AVX2 /O2)
endif()

# ---------------------------------------------------------------------------------
# Native self-test. Checks the things that must hold before the .NET side is allowed
# to have an opinion: struct sizes, the closed form against known values, and
# put-call parity.
# ---------------------------------------------------------------------------------
enable_testing()
add_executable(native_selftest tests/selftest.cpp src/engine.cpp)
target_include_directories(native_selftest PRIVATE include src)
target_compile_definitions(native_selftest PRIVATE PJ_HARDENED=1)
if(MSVC)
  target_compile_options(native_selftest PRIVATE /fp:precise /O2)
endif()
add_test(NAME native_selftest COMMAND native_selftest)
