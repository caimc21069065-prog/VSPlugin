// Deliberately broken C++ used to verify the extractor never throws and keeps working.
#include "sample.h"

#define BROKEN_MACRO(x) \
    ((x) + 1)

} } ; ) ( [ ] . -> :: ~ ! ?

class Survives
{
public:
    void Inside();

private:
    int value;
};

int ValidFunction(int value)
{
    return value;
}

#if 0
class Ghost
{
    void NotIndexed();
};
#endif

namespace broken_ns
{
    struct Fine
    {
        int okay;
    };
}

class AfterIf0
{
public:
    void Visible();
};

const char* escaped = "ok";
int AfterBrokenString = 1;
void TruncatedFunction(
    int a,
    int b
