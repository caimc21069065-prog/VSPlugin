// Third-party style header (Win32 / Qt / COM flavoured) used to exercise tolerant extraction.
#pragma once

#ifndef MESSY_THIRD_PARTY_H
#define MESSY_THIRD_PARTY_H

#define MESSY_EXPORT __declspec(dllexport)
#define MESSY_CALL __cdecl
#define MESSY_BEGIN_NAMESPACE namespace messy {
#define MESSY_END_NAMESPACE }

#ifdef __cplusplus
extern "C" {
#endif

typedef struct _MESSY_HANDLE *MESSY_HANDLE;

typedef HRESULT (MESSY_CALL *MESSY_CALLBACK)(int code, void* context);

MESSY_EXPORT int MESSY_CALL MessyApiFunction(int value);

#ifdef __cplusplus
}
#endif

MESSY_BEGIN_NAMESPACE

class MESSY_EXPORT MessyWidget
{
public:
    MESSY_CALL MessyWidget();
    virtual ~MessyWidget();

    [[nodiscard]] int Compute(int value) const;
    operator bool() const;
    MessyWidget& operator=(const MessyWidget& other);

    void MultiLine(
        int first,
        const char* second,
        double third = 3.5) noexcept;

private:
    static int s_instanceCount;
    unsigned int flags : 8;
    struct
    {
        int nestedX;
        int nestedY;
    } anonymousMember;
};

MESSY_END_NAMESPACE

#endif
