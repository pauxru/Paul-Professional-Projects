#include <iostream>
#include <string>

#include "test_framework.hpp"

int main(int argc, char** argv) {
    std::string filter = argc > 1 ? argv[1] : "";
    int passed = 0;
    int failed = 0;
    for (const auto& test : testing::registry()) {
        if (!filter.empty() && test.name.find(filter) == std::string::npos) continue;
        try {
            test.fn();
            ++passed;
            std::cout << "[  PASS  ] " << test.name << "\n";
        } catch (const testing::Failure& f) {
            ++failed;
            std::cout << "[  FAIL  ] " << test.name << "\n           " << f.message << "\n";
        } catch (const std::exception& e) {
            ++failed;
            std::cout << "[  FAIL  ] " << test.name << "\n           unexpected exception: " << e.what() << "\n";
        }
    }
    std::cout << "\n" << passed << " passed, " << failed << " failed, " << (passed + failed) << " total\n";
    return failed == 0 ? 0 : 1;
}
