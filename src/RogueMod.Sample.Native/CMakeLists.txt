cmake_minimum_required(VERSION 3.22)

set(TARGET HelloNativeMod)

add_library(${TARGET} SHARED src/SampleNativeMod.cpp)
target_include_directories(${TARGET} PRIVATE "${CMAKE_CURRENT_LIST_DIR}/../RogueMod.Sdk/Native/include")
target_compile_features(${TARGET} PRIVATE cxx_std_23)
target_compile_definitions(${TARGET} PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(${TARGET} PROPERTIES PREFIX "" OUTPUT_NAME "main")

set(PACKAGE_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../.artifacts/packages/native/${CMAKE_BUILD_TYPE}/sample.hello-native")
add_custom_target(PackageHelloNativeMod
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${PACKAGE_ROOT}/dlls"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:${TARGET}>" "${PACKAGE_ROOT}/dlls/main.dll"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CMAKE_CURRENT_LIST_DIR}/mod.json" "${PACKAGE_ROOT}/mod.json"
    DEPENDS ${TARGET} "${CMAKE_CURRENT_LIST_DIR}/mod.json"
    COMMENT "Packaging sample.hello-native")
