cmake_minimum_required(VERSION 3.22)

if(NOT ROGUEMOD_NATIVE_INCLUDE_DIR)
    set(ROGUEMOD_NATIVE_INCLUDE_DIR "$ENV{ROGUEMOD_NATIVE_INCLUDE_DIR}" CACHE PATH
        "Include root containing the RogueMod/ and UE4SS/ SDK headers")
endif()
if(NOT ROGUEMOD_NATIVE_INCLUDE_DIR)
    message(FATAL_ERROR
        "ROGUEMOD_NATIVE_INCLUDE_DIR is not set. Point it at the build/native/include directory of the RogueMod.Sdk package.")
endif()

set(TARGET RogueMod.NativeMod)

add_library(${TARGET} SHARED src/RogueMod.NativeMod.cpp)
target_include_directories(${TARGET} PRIVATE "${ROGUEMOD_NATIVE_INCLUDE_DIR}")
target_compile_features(${TARGET} PRIVATE cxx_std_23)
target_compile_definitions(${TARGET} PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(${TARGET} PROPERTIES PREFIX "" OUTPUT_NAME "main")

set(PACKAGE_ROOT "${CMAKE_CURRENT_LIST_DIR}/.artifacts/packages/native/Game__Shipping__Win64/sample.native-mod")
add_custom_target(PackageRogueNativeMod
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${PACKAGE_ROOT}/dlls"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:${TARGET}>" "${PACKAGE_ROOT}/dlls/main.dll"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CMAKE_CURRENT_LIST_DIR}/mod.json" "${PACKAGE_ROOT}/mod.json"
    DEPENDS ${TARGET} "${CMAKE_CURRENT_LIST_DIR}/mod.json"
    COMMENT "Packaging sample.native-mod")
