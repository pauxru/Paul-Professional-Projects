cmake_minimum_required(VERSION 3.20)
project(constrained_decoding CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
  add_compile_options(/W4 /permissive- /EHsc)
else()
  add_compile_options(-Wall -Wextra -Wpedantic)
endif()

set(CDEC_CORE_SOURCES
  src/core/json.cpp
  src/core/automaton.cpp
  src/core/regex.cpp
  src/core/schema.cpp
  src/core/mask.cpp
)

add_library(cdec_core STATIC ${CDEC_CORE_SOURCES})
target_include_directories(cdec_core PUBLIC src)

# The shared library is what Python loads through ctypes. Keeping it separate
# from the static core means the C++ tests link the core directly and are not
# affected by export visibility.
add_library(cdec SHARED src/bindings/capi.cpp ${CDEC_CORE_SOURCES})
target_include_directories(cdec PUBLIC src)
target_compile_definitions(cdec PRIVATE CDEC_BUILD_SHARED)
set_target_properties(cdec PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
)

add_executable(cdec_tests
  tests/cpp/main.cpp
  tests/cpp/test_core.cpp
  tests/cpp/test_schema_mask.cpp
)
target_link_libraries(cdec_tests PRIVATE cdec_core)

add_executable(cdec_bench bench/bench_main.cpp)
target_link_libraries(cdec_bench PRIVATE cdec_core)

enable_testing()
add_test(NAME cpp_unit_tests COMMAND cdec_tests)
