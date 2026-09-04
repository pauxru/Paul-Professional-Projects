// A very small test registry. Written by hand rather than pulled in, because
// no C++ package manager is available in this environment and a test framework
// is not where the interesting engineering is.
#pragma once

#include <functional>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

namespace testing {

struct TestCase {
    std::string name;
    std::function<void()> fn;
};

inline std::vector<TestCase>& registry() {
    static std::vector<TestCase> tests;
    return tests;
}

struct Registrar {
    Registrar(const std::string& name, std::function<void()> fn) { registry().push_back({name, std::move(fn)}); }
};

struct Failure {
    std::string message;
};

inline void fail(const std::string& expr, const char* file, int line, const std::string& extra = "") {
    std::ostringstream os;
    os << file << ":" << line << ": " << expr;
    if (!extra.empty()) os << "  [" << extra << "]";
    throw Failure{os.str()};
}

}  // namespace testing

#define TEST(name)                                                       \
    static void name();                                                  \
    static ::testing::Registrar registrar_##name(#name, name);           \
    static void name()

#define CHECK(cond)                                                      \
    do {                                                                 \
        if (!(cond)) ::testing::fail("CHECK failed: " #cond, __FILE__, __LINE__); \
    } while (0)

#define CHECK_EQ(a, b)                                                   \
    do {                                                                 \
        auto va = (a);                                                   \
        auto vb = (b);                                                   \
        if (!(va == vb)) {                                               \
            std::ostringstream os;                                       \
            os << "got=" << va << " want=" << vb;                        \
            ::testing::fail("CHECK_EQ failed: " #a " == " #b, __FILE__, __LINE__, os.str()); \
        }                                                                \
    } while (0)
